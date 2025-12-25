$root = "C:\FTP\Shares\releases\1.0.0"
$manifestPath = Join-Path $root "manifest.txt"

# Lấy danh sách file, tính hash, và tạo đường dẫn tương đối
$entries = Get-ChildItem -Path $root -Recurse -File | ForEach-Object {
    $relativePath = $_.FullName.Substring($root.Length + 1).Replace("\", "/")
    $hash = Get-FileHash $_.FullName -Algorithm SHA256
    "$($hash.Hash.ToLower())`t$relativePath"
}

# Sắp xếp theo đường dẫn tương đối
$sortedEntries = $entries | Sort-Object { ($_ -split "`t")[1] }

# Ghi ra file manifest UTF-8
$sortedEntries | Out-File -FilePath $manifestPath -Encoding utf8

Write-Host "✅ Manifest đã được tạo tại: $manifestPath"