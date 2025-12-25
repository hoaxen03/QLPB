param(
    [string]$OutPath = (Join-Path $env:APPDATA "ReadFileFTP\ftp_credentials.dat")
)

function Read-SecureStringPlain {
    param([System.Security.SecureString]$ss)
    if ($null -eq $ss) { return "" }
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($ss)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringUni($bstr) } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}

Write-Host "Output path:" $OutPath
$ftpHost = Read-Host "Host (e.g. ftp.example.com)"
$ftpPort = Read-Host "Port (default 21)"
if ([string]::IsNullOrWhiteSpace($ftpPort)) { $ftpPort = "21" }
$ftpUser = Read-Host "User"
$pwdSecure = Read-Host "Password (input hidden)" -AsSecureString
$ftpPwd = Read-SecureStringPlain -ss $pwdSecure
$ftpRemote = Read-Host "Remote root (default /)"
if ([string]::IsNullOrWhiteSpace($ftpRemote)) { $ftpRemote = "/" }

$content = @"
host=$ftpHost
port=$ftpPort
user=$ftpUser
password=$ftpPwd
remote=$ftpRemote
encryption=dpapi
"@.Trim()

$plainBytes = [System.Text.Encoding]::UTF8.GetBytes($content)

# Try using .NET ProtectedData first (works on Windows PowerShell / .NET that exposes it)
$protected = $null
try {
    if ([type]::GetType("System.Security.Cryptography.ProtectedData", $false) -ne $null) {
        $protected = [System.Security.Cryptography.ProtectedData]::Protect($plainBytes, $null, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
    } else {
        throw "ProtectedData type not found"
    }
}
catch {
    # Fallback: Add a small P/Invoke wrapper for CryptProtectData (Windows DPAPI)
    $cs = @"
using System;
using System.Runtime.InteropServices;

public static class DPAPIFallback {
    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB { public int cbData; public IntPtr pbData; }

    [DllImport("crypt32.dll", SetLastError=true, CharSet=CharSet.Auto)]
    private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string szDataDescr, IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

    public static byte[] Protect(byte[] data) {
        var inBlob = new DATA_BLOB();
        inBlob.cbData = data.Length;
        inBlob.pbData = Marshal.AllocHGlobal(data.Length);
        try {
            Marshal.Copy(data, 0, inBlob.pbData, data.Length);
            var outBlob = new DATA_BLOB();
            bool ok = CryptProtectData(ref inBlob, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref outBlob);
            if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var result = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
            Marshal.FreeHGlobal(outBlob.pbData);
            return result;
        }
        finally {
            Marshal.FreeHGlobal(inBlob.pbData);
        }
    }
}
"@

    Add-Type -TypeDefinition $cs -Language CSharp -ErrorAction Stop
    $protected = [DPAPIFallback]::Protect($plainBytes)
}

if ($null -eq $protected) {
    Write-Error "Failed to protect credentials (DPAPI)."
    exit 1
}

$b64 = [Convert]::ToBase64String($protected)

# ensure directory exists and write file
$dir = [System.IO.Path]::GetDirectoryName($OutPath)
if (-not [string]::IsNullOrWhiteSpace($dir) -and -not (Test-Path $dir)) { New-Item -Path $dir -ItemType Directory -Force | Out-Null }

Set-Content -Path $OutPath -Value $b64 -Encoding UTF8

# try set hidden attribute
try {
    $item = Get-Item $OutPath
    $item.Attributes = $item.Attributes -bor [System.IO.FileAttributes]::Hidden
} catch { }

Write-Host "Wrote DPAPI credentials to: $OutPath"
Write-Host "Note: DPAPI CurrentUser encrypted file can only be decrypted by the same Windows user on this machine."