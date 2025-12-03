using FluentFTP;
using FluentFTP.Helpers;
using System;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace ReadFileFTP
{
    public partial class Form1 : Form
    {
        // lưu client để tái sử dụng
        private FtpClient _client;
        // để huỷ các tác vụ nếu cần
        private CancellationTokenSource _cts;

        private string _credentialsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ReadFileFTP", "ftp_credentials.dat");
        private FtpSettings _ftpSettings;


        // Model
        private class FtpSettings
        {
            public string Host { get; set; }
            public int Port { get; set; } = 21;
            public string User { get; set; }
            public string Password { get; set; }
            public string Remote { get; set; } = "/";
            public string Encryption { get; set; } = "none"; // "none" or "dpapi"
        }

        /// <summary>
        /// Gọi an toàn một Action trên UI thread (nếu cần sẽ Invoke).
        /// </summary>
        private void SafeInvoke(Action act)
        {
            if (act == null) return;
            try
            {
                if (this.IsHandleCreated && this.InvokeRequired)
                    this.BeginInvoke(act);
                else
                    act();
            }
            catch
            {
                // ignore errors to avoid crash from logging/cleanup calls
            }
        }

        /// <summary>
        /// Gọi an toàn một Func<T> trên UI thread và trả về giá trị.
        /// Nếu InvokeRequired thì dùng Invoke và trả về kết quả.
        /// NOTE: dùng cẩn thận vì Invoke (synchronous) có thể block UI nếu gọi từ UI thread — ở trường hợp đó InvokeRequired sẽ false.
        /// </summary>
        private T SafeInvoke<T>(Func<T> func)
        {
            if (func == null) return default!;
            try
            {
                if (this.IsHandleCreated && this.InvokeRequired)
                    return (T)this.Invoke(func);
                else
                    return func();
            }
            catch
            {
                return default!;
            }
        }


        public Form1()
        {
            InitializeComponent();
        }

        private void Log(string message)
        {
            try
            {
                var text = $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}";

                if (txtLog == null || txtLog.IsDisposed) return;

                if (txtLog.InvokeRequired)
                {
                    // BeginInvoke để không block thread nền
                    txtLog.BeginInvoke((Action)(() =>
                    {
                        try
                        {
                            txtLog.AppendText(text);
                            // auto scroll to bottom
                            txtLog.SelectionStart = txtLog.Text.Length;
                            txtLog.ScrollToCaret();
                        }
                        catch { /* ignore UI errors */ }
                    }));
                }
                else
                {
                    txtLog.AppendText(text);
                    txtLog.SelectionStart = txtLog.Text.Length;
                    txtLog.ScrollToCaret();
                }
            }
            catch { /* ignore logging errors to avoid crashing background tasks */ }
        }

        // Helper to detect manifest/checksum file names we should skip during sync
        private static bool IsManifestRelPath(string rel)
        {
            if (string.IsNullOrWhiteSpace(rel)) return false;
            var name = Path.GetFileName(rel).ToLowerInvariant();
            return name == "manifest.txt"
                || name == "checksums.txt"
                || name == "hashes.txt"
                || name == "sha256sums.txt"
                || name == "checksums.sha256";
        }



        private async void BtnConnect_Click(object sender, EventArgs e)
        {
            // disable button & change cursor early
            btnConnect.Enabled = false;
            var prevCursor = Cursor;
            Cursor = Cursors.WaitCursor;

            try
            {
                // File-only credentials
                if (_ftpSettings == null)
                {
                    MessageBox.Show("Không tìm thấy file credentials hợp lệ. Vui lòng đặt file credentials (ftp_credentials.txt/.dat).");
                    Log("Chưa có _ftpSettings - từ chối kết nối (chỉ dùng credentials từ file).");
                    SetStatus("Thiếu credentials từ file.", true);
                    return;
                }

                var host = _ftpSettings.Host?.Trim();
                var port = (_ftpSettings.Port > 0 && _ftpSettings.Port <= 65535) ? _ftpSettings.Port : 21;
                var user = _ftpSettings.User ?? "";
                var pass = _ftpSettings.Password ?? "";

                Log($"Sử dụng credentials từ file: host={host}, port={port}, user={user}");

                if (string.IsNullOrWhiteSpace(host))
                {
                    MessageBox.Show("Chưa có host FTP trong file credentials.");
                    return;
                }

                // show starting status
                Log($"Chuẩn bị kết nối tới {host}:{port} ...");
                SetStatus($"Đang kết nối tới {host}:{port}...");

                try { _client?.Dispose(); } catch { }

                // Build client (try Explicit TLS first)
                _client = new FluentFTP.FtpClient(host)
                {
                    Credentials = new System.Net.NetworkCredential(user, pass),
                    Port = port,
                    EncryptionMode = FluentFTP.FtpEncryptionMode.Explicit,
                    ValidateAnyCertificate = true // dev: true, production: consider validation
                };
                // Force binary transfer to avoid ASCII/EOL conversion
                ForceBinaryTransferMode(_client);

                async Task DoConnectAndPostConnectAsync(FluentFTP.FtpClient client)
                {
                    var miConnectAsync = client.GetType().GetMethod("ConnectAsync", Type.EmptyTypes);
                    if (miConnectAsync != null)
                    {
                        var t = (Task)miConnectAsync.Invoke(client, null);
                        await t;
                    }
                    else
                    {
                        var miConnect = client.GetType().GetMethod("Connect", Type.EmptyTypes);
                        if (miConnect != null) miConnect.Invoke(client, null);
                        else throw new NotSupportedException("Không tìm thấy Connect/ConnectAsync trên client FluentFTP.");
                    }
                }

                // Try connect with TLS, fallback to Plain if AUTH TLS error
                try
                {
                    await DoConnectAndPostConnectAsync(_client);
                    Log($"Đã kết nối tới {host}:{port} (TLS/Explicit).");
                }
                catch (TargetInvocationException tie) when (
                         (tie.InnerException?.Message ?? "").IndexOf("AUTH", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         (tie.InnerException?.Message ?? "").IndexOf("AUTH TLS", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         (tie.InnerException?.Message ?? "").IndexOf("Use AUTH first", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Log("AUTH TLS thất bại -> thử plain FTP (EncryptionMode.None).");
                    try { _client?.Dispose(); } catch { }
                    _client = new FluentFTP.FtpClient(host)
                    {
                        Credentials = new System.Net.NetworkCredential(user, pass),
                        Port = port,
                        EncryptionMode = FluentFTP.FtpEncryptionMode.None,
                        ValidateAnyCertificate = true
                    };
                    // Force binary on plain mode too
                    ForceBinaryTransferMode(_client);

                    await DoConnectAndPostConnectAsync(_client);
                    Log($"Đã kết nối tới {host}:{port} (Plain FTP).");
                }
                catch (Exception ex)
                {
                    var innerMsg = (ex is TargetInvocationException t2 && t2.InnerException != null) ? t2.InnerException.Message : ex.Message;
                    if (innerMsg.IndexOf("AUTH", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        innerMsg.IndexOf("AUTH TLS", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        innerMsg.IndexOf("Use AUTH first", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        Log("Kết nối ban đầu lỗi liên quan AUTH -> thử plain FTP.");
                        try { _client?.Dispose(); } catch { }
                        _client = new FluentFTP.FtpClient(host)
                        {
                            Credentials = new System.Net.NetworkCredential(user, pass),
                            Port = port,
                            EncryptionMode = FluentFTP.FtpEncryptionMode.None,
                            ValidateAnyCertificate = true
                        };
                        // Force binary on plain mode too
                        ForceBinaryTransferMode(_client);

                        await DoConnectAndPostConnectAsync(_client);
                        Log($"Đã kết nối tới {host}:{port} (Plain FTP).");
                    }
                    else
                    {
                        throw;
                    }
                }

                SetStatus($"Đã kết nối tới {host}:{port}");
                SafeInvoke(() => btnConnect.Text = "Đã kết nối");

                var remotePath = "/";
                try { if (!string.IsNullOrWhiteSpace(txtRemote?.Text)) remotePath = txtRemote.Text.Trim(); } catch { }
                if (string.IsNullOrEmpty(remotePath)) remotePath = "/";
                await RefreshListAsync(remotePath);
            }
            catch (Exception ex)
            {
                var realEx = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                Log("Kết nối thất bại: " + realEx.Message);
                MessageBox.Show("Kết nối thất bại: " + realEx.Message);
                SetStatus("Kết nối thất bại: " + realEx.Message, true);
            }
            finally
            {
                Cursor = prevCursor;
                btnConnect.Enabled = true;
            }
        }
        private async void BtnList_Click(object sender, EventArgs e)
        {
            var remotePath = txtRemote.Text.Trim();
            if (string.IsNullOrEmpty(remotePath)) remotePath = "/";
            await RefreshListAsync(remotePath);
        }

        private void BtnBrowseLocal_Click(object sender, EventArgs e)
        {
            using (var fbd = new FolderBrowserDialog())
            {
                if (fbd.ShowDialog() == DialogResult.OK)
                {
                    txtLocal.Text = fbd.SelectedPath; // đảm bảo txtLocal là tên control trong Designer
                }
            }
        }

        private async void BtnDownload_Click(object sender, EventArgs e)
        {
            await DownloadSelectedItemsAsync(deleteAfter: false);
        }

        private async void BtnMove_Click(object sender, EventArgs e)
        {
            var r = MessageBox.Show("Xác nhận MOVE: các file/ thư mục được chọn sẽ được tải về và xóa trên server?", "Xác nhận", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (r != DialogResult.Yes) return;
            await DownloadSelectedItemsAsync(deleteAfter: true);
        }

        private async void BtnCompareHash_Click(object sender, EventArgs e)
        {
            if (lvFiles.SelectedItems.Count == 0)
            {
                MessageBox.Show("Chọn một mục (file hoặc thư mục) trong danh sách để kiểm tra hash.");
                return;
            }

            var sel = lvFiles.SelectedItems[0];

            // build candidate/name như trước
            string candidate = null;
            string displayName = sel.Text;
            if (sel.Tag != null)
            {
                var t = sel.Tag;
                var fullProp = t.GetType().GetProperty("FullName");
                var nameProp = t.GetType().GetProperty("Name");
                var full = fullProp?.GetValue(t)?.ToString();
                var name = nameProp?.GetValue(t)?.ToString();
                candidate = full ?? name;
                if (!string.IsNullOrEmpty(name)) displayName = name;
            }

            var currentRemoteBase = txtRemote?.Text?.Trim() ?? "/";
            var foundRemote = await FindExistingRemoteFileAsync(candidate, currentRemoteBase, displayName);
            if (foundRemote == null)
            {
                MessageBox.Show("Không tìm thấy đường dẫn hợp lệ trên server cho mục: " + displayName);
                return;
            }

            // determine local path base
            var localFolder = txtLocal?.Text?.Trim();
            if (string.IsNullOrEmpty(localFolder)) localFolder = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            Directory.CreateDirectory(localFolder);

            // prepare cancellation for this operation
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            // UI lock
            SafeInvoke(() =>
            {
                SetUiBusy(true);
                progressBar.Value = 0;
                if (btnCancel != null) btnCancel.Enabled = true;
                SetStatus("Kiểm tra hash...");
            });
            var prevCursor = Cursor;
            Cursor = Cursors.WaitCursor;

            try
            {
                // Detect remote type (file or directory)
                bool isDir = await IsRemoteDirectoryAsync(foundRemote);

                if (!isDir)
                {
                    // ---------- FILE FLOW ----------
                    var localPath = Path.Combine(localFolder, Path.GetFileName(foundRemote));
                    if (!File.Exists(localPath))
                    {
                        var r = SafeInvoke(() => MessageBox.Show(this, "File đích chưa có trên local. Tải tạm file từ FTP để kiểm tra?", "Tải tạm", MessageBoxButtons.YesNo));
                        if (r != DialogResult.Yes) return;

                        await DownloadFileAsync(foundRemote, localPath, deleteAfter: false);
                    }

                    Log("Bắt đầu so sánh SHA256 cho file: " + Path.GetFileName(localPath));
                    SetStatus("So sánh SHA256...");

                    string remoteHash = null;

                    // Try server-provided checksum via reflection (FluentFTP GetChecksumAsync)
                    try
                    {
                        var mi = _client?.GetType().GetMethod("GetChecksumAsync", new Type[] { typeof(string), typeof(FluentFTP.FtpHashAlgorithm) });
                        if (mi != null)
                        {
                            var t = (Task)mi.Invoke(_client, new object[] { foundRemote, FluentFTP.FtpHashAlgorithm.SHA256 });
                            await t;
                            var rp = t.GetType().GetProperty("Result");
                            var ch = rp?.GetValue(t);
                            if (ch != null)
                            {
                                var valProp = ch.GetType().GetProperty("Value");
                                remoteHash = valProp != null ? valProp.GetValue(ch)?.ToString() : ch.ToString();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log("Không lấy được checksum trực tiếp từ server: " + ex.Message);
                        remoteHash = null;
                    }

                    // Fallback: download temp copy and compute sha
                    string tmpDownloaded = null;
                    if (string.IsNullOrEmpty(remoteHash))
                    {
                        try
                        {
                            tmpDownloaded = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
                            Log("Server không trả SHA256 trực tiếp -> tải tạm để tính hash...");
                            await DownloadFileAsync(foundRemote, tmpDownloaded, deleteAfter: false);
                            remoteHash = await ComputeFileSha256Async(tmpDownloaded, null, token);
                        }
                        catch (OperationCanceledException)
                        {
                            SafeInvoke(() => MessageBox.Show(this, "Đã hủy tải tạm file.", "Hủy", MessageBoxButtons.OK, MessageBoxIcon.Information));
                            return;
                        }
                        catch (Exception ex)
                        {
                            Log("Không thể tải tạm file để tính hash: " + ex.Message);
                            SafeInvoke(() => MessageBox.Show(this, "Không thể lấy hash từ server: " + ex.Message, "Lỗi", MessageBoxButtons.OK, MessageBoxIcon.Error));
                            return;
                        }
                        finally
                        {
                            try { if (tmpDownloaded != null && File.Exists(tmpDownloaded)) File.Delete(tmpDownloaded); } catch { }
                        }
                    }

                    // compute local file hash with progress
                    string localHash;
                    try
                    {
                        var localProgress = new Progress<long>(b =>
                        {
                            try
                            {
                                var total = new FileInfo(localPath).Length;
                                if (total > 0)
                                {
                                    var pct = (int)Math.Min(100, (b * 100.0 / total));
                                    SafeInvoke(() => progressBar.Value = pct);
                                }
                            }
                            catch { }
                        });
                        localHash = await ComputeFileSha256Async(localPath, localProgress, token);
                    }
                    catch (OperationCanceledException)
                    {
                        SafeInvoke(() => MessageBox.Show(this, "Đã hủy quá trình tính hash.", "Hủy", MessageBoxButtons.OK, MessageBoxIcon.Information));
                        return;
                    }

                    Log($"SHA256 (server) = {remoteHash}");
                    Log($"SHA256 (local)  = {localHash}");

                    if (string.Equals(remoteHash, localHash, StringComparison.OrdinalIgnoreCase))
                    {
                        SafeInvoke(() => MessageBox.Show(this, "SHA256 trùng khớp ✓", "Kết quả", MessageBoxButtons.OK, MessageBoxIcon.Information));
                        SetStatus("SHA256 trùng khớp.");
                    }
                    else
                    {
                        SafeInvoke(() => MessageBox.Show(this, "SHA256 KHÔNG trùng khớp ✗", "Kết quả", MessageBoxButtons.OK, MessageBoxIcon.Warning));
                        SetStatus("SHA256 không khớp.", true);
                    }
                }
                else
                {
                    // ---------- DIRECTORY FLOW (ENHANCED with parent download) ----------
                    var folderName = Path.GetFileName(foundRemote.TrimEnd('/', '\\'));
                    var localDir = Path.Combine(localFolder, folderName);

                    if (!Directory.Exists(localDir))
                    {
                        var r = SafeInvoke(() => MessageBox.Show(this, $"Thư mục đích '{folderName}' chưa có trên local. Tải thư mục từ FTP về để kiểm tra?", "Tải thư mục", MessageBoxButtons.YesNo));
                        if (r != DialogResult.Yes) return;

                        try
                        {
                            await DownloadDirectoryAsync(foundRemote, localDir, deleteAfter: false);
                        }
                        catch (OperationCanceledException)
                        {
                            SafeInvoke(() => MessageBox.Show(this, "Đã hủy tải thư mục.", "Hủy", MessageBoxButtons.OK, MessageBoxIcon.Information));
                            return;
                        }
                        catch (Exception ex)
                        {
                            Log("Lỗi khi tải thư mục: " + ex.Message);
                            SafeInvoke(() => MessageBox.Show(this, "Lỗi khi tải thư mục: " + ex.Message, "Lỗi", MessageBoxButtons.OK, MessageBoxIcon.Error));
                            return;
                        }
                    }

                    // Try to find remote manifest (checksums)
                    // Try to find remote manifest (checksums)
                    string[] candidateManifests = new[] { "checksums.txt", "manifest.txt", "hashes.txt" };
                    string remoteManifestPath = null;
                    string localManifestTemp = null;

                    try
                    {
                        // 1) Thử dùng helper (dò theo listing)
                        var tmpFromListing = await FindRemoteManifestInDirectorySafeAsync(foundRemote);
                        if (!string.IsNullOrEmpty(tmpFromListing) && File.Exists(tmpFromListing))
                        {
                            var fname = Path.GetFileName(tmpFromListing) ?? "";
                            var idx = fname.IndexOf('_');
                            var originalName = (idx >= 0 && idx + 1 < fname.Length) ? fname.Substring(idx + 1) : fname;
                            remoteManifestPath = NormalizeFtpPath(foundRemote.TrimEnd('/') + "/" + originalName);
                            localManifestTemp = tmpFromListing;
                            Log("Tìm thấy manifest (từ listing): " + remoteManifestPath + " -> local: " + localManifestTemp);
                        }

                        // 2) Fallback: nếu helper không tìm -> thử trực tiếp các path candidate (RETR dù file không xuất hiện trong listing)
                        if (remoteManifestPath == null)
                        {
                            foreach (var name in candidateManifests)
                            {
                                var candidatePath = NormalizeFtpPath(foundRemote.TrimEnd('/') + "/" + name);
                                var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + "_" + name);
                                try
                                {
                                    Log("Thử trực tiếp candidate manifest: " + candidatePath);
                                    var ok = await DownloadRemoteFileSafeAsync(candidatePath, tmp);
                                    if (!ok) { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } continue; }

                                    // validate nhanh nội dung giống manifest (sha<TAB>path)
                                    var lines = File.ReadAllLines(tmp, Encoding.UTF8);
                                    var regex = new System.Text.RegularExpressions.Regex(@"^[0-9a-fA-F]{64}\t.+");
                                    bool looksLikeManifest = lines.Take(10).Any(l => !string.IsNullOrWhiteSpace(l) && regex.IsMatch(l.Trim()));

                                    if (looksLikeManifest)
                                    {
                                        remoteManifestPath = candidatePath;
                                        localManifestTemp = tmp;
                                        Log("Tìm thấy manifest (direct RETR): " + remoteManifestPath + " -> local: " + localManifestTemp);
                                        break;
                                    }
                                    else
                                    {
                                        Log("File tải về không giống manifest, xóa tmp: " + tmp);
                                        try { File.Delete(tmp); } catch { }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Log("Lỗi thử tải candidate '" + candidatePath + "': " + ex.Message);
                                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log("Lỗi khi tìm manifest remote: " + ex.Message);
                    }


                    if (remoteManifestPath == null)
                    {
                        SafeInvoke(() => MessageBox.Show(this, "Không tìm thấy manifest checksums (checksums.txt/manifest.txt) trong thư mục remote. Không thể tự động so sánh.", "Không có manifest", MessageBoxButtons.OK, MessageBoxIcon.Information));
                        SetStatus("Không có manifest remote để so sánh.", true);
                        return;
                    }

                    // Determine if the manifest is in a parent folder
                    var foundRemoteNormalized = foundRemote.TrimEnd('/', '\\') + "/";
                    bool manifestIsParent = !remoteManifestPath.StartsWith(foundRemoteNormalized, StringComparison.OrdinalIgnoreCase);

                    // If manifest in parent and local parent not available, ask to download parent
                    string localParentTempFolder = null;
                    string localParentManifestPath = null;
                    if (manifestIsParent)
                    {
                        var parentRemoteFolder = Path.GetDirectoryName(remoteManifestPath.Replace('\\', '/'))?.Replace('\\', '/');
                        if (!string.IsNullOrEmpty(parentRemoteFolder))
                        {
                            // If parentRemoteFolder equals root or '.', normalize
                            if (!parentRemoteFolder.StartsWith("/")) parentRemoteFolder = "/" + parentRemoteFolder;

                            // Ask user to download parent directory to temp for child-vs-parent check
                            var ask = SafeInvoke(() => MessageBox.Show(this, $"Manifest được tìm thấy ở thư mục mẹ ({parentRemoteFolder}).\nBạn có muốn tải thư mục mẹ xuống tạm để so sánh con với manifest không? (có thể tốn thời gian)", "Tải thư mục mẹ?", MessageBoxButtons.YesNo, MessageBoxIcon.Question));
                            if (ask == DialogResult.Yes)
                            {
                                // create temp folder
                                localParentTempFolder = Path.Combine(Path.GetTempPath(), "parent_" + Guid.NewGuid().ToString());
                                Directory.CreateDirectory(localParentTempFolder);
                                try
                                {
                                    SetStatus("Đang tải thư mục mẹ (tạm) để kiểm tra...");
                                    Log("Tải thư mục mẹ từ " + parentRemoteFolder + " về " + localParentTempFolder);
                                    await DownloadDirectoryAsync(parentRemoteFolder, localParentTempFolder, deleteAfter: false);
                                    // localManifestTemp already holds manifest file when downloaded earlier if candidate tried
                                    // But manifest was downloaded from remoteManifestPath into localManifestTemp. We'll set localParentManifestPath to that.
                                    localParentManifestPath = localManifestTemp;
                                }
                                catch (Exception ex)
                                {
                                    Log("Không thể tải thư mục mẹ: " + ex.Message);
                                    SafeInvoke(() => MessageBox.Show(this, "Không thể tải thư mục mẹ: " + ex.Message, "Lỗi", MessageBoxButtons.OK, MessageBoxIcon.Error));
                                    // cleanup
                                    try { if (Directory.Exists(localParentTempFolder)) Directory.Delete(localParentTempFolder, true); } catch { }
                                    localParentTempFolder = null;
                                    localParentManifestPath = null;
                                }
                            }
                        }
                    }

                    // --- Generate local manifest for the downloaded localDir (child) ---
                    SetStatus("Tạo manifest cho thư mục local con...");
                    var localManifestGenerated = Path.Combine(Path.GetTempPath(), "manifest_local_" + Guid.NewGuid().ToString() + ".txt");
                    try
                    {
                        var genProgress = new Progress<(string relativePath, long fileBytes)>(p =>
                        {
                            SafeInvoke(() => toolStripStatusLabelStatus.Text = $"Tạo manifest: {p.relativePath} ({FormatBytes(p.fileBytes)})");
                        });
                        await GenerateDirectoryManifestAsync(localDir, localManifestGenerated, includeSubdirs: true, perFileProgress: genProgress, ct: token);
                    }
                    catch (OperationCanceledException)
                    {
                        SafeInvoke(() => MessageBox.Show(this, "Đã hủy tạo manifest cho thư mục local.", "Hủy", MessageBoxButtons.OK, MessageBoxIcon.Information));
                        return;
                    }
                    catch (Exception ex)
                    {
                        Log("Lỗi khi tạo manifest local: " + ex.Message);
                        SafeInvoke(() => MessageBox.Show(this, "Lỗi khi tạo manifest local: " + ex.Message, "Lỗi", MessageBoxButtons.OK, MessageBoxIcon.Error));
                        try { if (File.Exists(localManifestGenerated)) File.Delete(localManifestGenerated); } catch { }
                        return;
                    }

                    // --- Compute directory-hash from both manifests ---
                    string remoteManifestHash = null;
                    string localManifestHash = null;
                    try
                    {
                        remoteManifestHash = ComputeDirectoryHashFromManifestFile(localManifestTemp);
                    }
                    catch (Exception ex)
                    {
                        Log("Không thể tính hash từ manifest remote: " + ex.Message);
                    }

                    try
                    {
                        localManifestHash = ComputeDirectoryHashFromManifestFile(localManifestGenerated);
                    }
                    catch (Exception ex)
                    {
                        Log("Không thể tính hash từ manifest local: " + ex.Message);
                    }

                    Log($"Directory-hash (remote manifest) = {remoteManifestHash}");
                    Log($"Directory-hash (local  manifest) = {localManifestHash}");

                    // --- Verify directory against remote manifest (detailed file-level) ---
                    SetStatus("Đang kiểm tra thư mục theo manifest...");
                    var verifyProgress = new Progress<(string relativePath, int state)>(s =>
                    {
                        try
                        {
                            SafeInvoke(() =>
                            {
                                if (!string.IsNullOrEmpty(s.relativePath))
                                {
                                    toolStripStatusLabelStatus.Text = $"{s.relativePath} - {(s.state == 2 ? "OK" : s.state == 1 ? "MISMATCH" : s.state == 3 ? "MISSING" : s.state == 4 ? "EXTRA" : "...")}";
                                }
                            });
                        }
                        catch { }
                    });

                    (List<string> missing, List<string> mismatched, List<string> extra) result;
                    try
                    {
                        result = await VerifyDirectoryAgainstManifestAsync(localDir, localManifestTemp, verifyProgress, token);
                    }
                    catch (OperationCanceledException)
                    {
                        SafeInvoke(() => MessageBox.Show(this, "Đã hủy kiểm tra.", "Hủy", MessageBoxButtons.OK, MessageBoxIcon.Information));
                        return;
                    }

                    // If we downloaded parent, run child-vs-parent check using downloaded parent folder + manifest
                    if (!string.IsNullOrEmpty(localParentTempFolder) && !string.IsNullOrEmpty(localParentManifestPath))
                    {
                        try
                        {
                            SetStatus("So sánh thư mục con với manifest thư mục mẹ...");
                            var childRelative = folderName; // assume child folder name relative to parent
                            var childCompareProgress = new Progress<(string relativePath, int state)>(s =>
                            {
                                SafeInvoke(() =>
                                {
                                    if (!string.IsNullOrEmpty(s.relativePath))
                                        toolStripStatusLabelStatus.Text = $"Child: {s.relativePath} - {(s.state == 2 ? "OK" : s.state == 1 ? "MISMATCH" : s.state == 3 ? "MISSING" : s.state == 4 ? "EXTRA" : "...")}";
                                });
                            });

                            var childRes = await VerifyChildAgainstParentManifestAsync(localParentManifestPath, /*parentManifestPath*/
                                                                                      localParentTempFolder, /*parentRootPath*/
                                                                                      folderName, /*childRelativePath*/
                                                                                      localDir, /*childDirPath*/
                                                                                      childCompareProgress, token);

                            var sbChild = new StringBuilder();
                            sbChild.AppendLine($"So sánh thư mục con '{folderName}' với manifest của thư mục mẹ:");
                            sbChild.AppendLine($"Thiếu: {childRes.missing.Count}    Khác: {childRes.mismatched.Count}    Thừa: {childRes.extra.Count}");
                            if (childRes.missing.Any())
                            {
                                sbChild.AppendLine();
                                sbChild.AppendLine("Các file thiếu:");
                                foreach (var x in childRes.missing.Take(20)) sbChild.AppendLine(" - " + x);
                            }
                            if (childRes.mismatched.Any())
                            {
                                sbChild.AppendLine();
                                sbChild.AppendLine("Các file mismatch:");
                                foreach (var x in childRes.mismatched.Take(20)) sbChild.AppendLine(" - " + x);
                            }
                            if (childRes.extra.Any())
                            {
                                sbChild.AppendLine();
                                sbChild.AppendLine("Các file thừa:");
                                foreach (var x in childRes.extra.Take(20)) sbChild.AppendLine(" - " + x);
                            }

                            SafeInvoke(() => MessageBox.Show(this, sbChild.ToString(), "Kết quả: Child vs Parent manifest", MessageBoxButtons.OK, MessageBoxIcon.Information));
                        }
                        catch (Exception ex)
                        {
                            Log("Không thể kiểm tra child-vs-parent: " + ex.Message);
                        }
                        finally
                        {
                            // cleanup downloaded parent folder
                            try { if (!string.IsNullOrEmpty(localParentTempFolder) && Directory.Exists(localParentTempFolder)) Directory.Delete(localParentTempFolder, true); } catch { }
                        }
                    }

                    // cleanup manifest temp for remote
                    try { if (localManifestTemp != null && File.Exists(localManifestTemp)) File.Delete(localManifestTemp); } catch { }

                    // Show main results
                    var sb = new StringBuilder();
                    sb.AppendLine($"Kiểm tra thư mục: {folderName}");
                    sb.AppendLine($"Directory-hash (remote manifest) = {remoteManifestHash}");
                    sb.AppendLine($"Directory-hash (local  manifest) = {localManifestHash}");
                    sb.AppendLine();
                    sb.AppendLine($"Thiếu: {result.missing.Count}    Khác (sha khác): {result.mismatched.Count}    Thừa: {result.extra.Count}");
                    if (result.missing.Any())
                    {
                        sb.AppendLine();
                        sb.AppendLine("Các file thiếu:");
                        foreach (var x in result.missing.Take(50)) sb.AppendLine(" - " + x);
                    }
                    if (result.mismatched.Any())
                    {
                        sb.AppendLine();
                        sb.AppendLine("Các file mismatch (sha khác):");
                        foreach (var x in result.mismatched.Take(50)) sb.AppendLine(" - " + x);
                    }
                    if (result.extra.Any())
                    {
                        sb.AppendLine();
                        sb.AppendLine("Các file thừa:");
                        foreach (var x in result.extra.Take(50)) sb.AppendLine(" - " + x);
                    }

                    SafeInvoke(() => MessageBox.Show(this, sb.ToString(), "Kết quả kiểm tra thư mục", MessageBoxButtons.OK, MessageBoxIcon.Information));
                    SetStatus("Hoàn tất kiểm tra thư mục.");
                }
            }
            catch (OperationCanceledException)
            {
                Log("Đã hủy kiểm tra hash theo yêu cầu.");
                SafeInvoke(() => MessageBox.Show(this, "Đã hủy kiểm tra.", "Hủy", MessageBoxButtons.OK, MessageBoxIcon.Information));
                SetStatus("Đã hủy kiểm tra.", true);
            }
            catch (Exception ex)
            {
                Log("Lỗi khi kiểm tra hash: " + ex.Message);
                SafeInvoke(() => MessageBox.Show(this, "Lỗi khi kiểm tra hash: " + ex.Message, "Lỗi", MessageBoxButtons.OK, MessageBoxIcon.Error));
                SetStatus("Lỗi kiểm tra hash.", true);
            }
            finally
            {
                SafeInvoke(() =>
                {
                    SetUiBusy(false);
                    if (btnCancel != null) btnCancel.Enabled = false;
                    try { progressBar.Value = 0; } catch { }
                    if (lblSpeed2 != null) lblSpeed2.Text = "";
                });
                Cursor = prevCursor;
                _cts?.Dispose();
                _cts = null;
            }
        }

        private async void LvFiles_DoubleClick(object sender, EventArgs e)
        {
            if (lvFiles.SelectedItems.Count == 0) return;

            var selected = lvFiles.SelectedItems[0];
            if (selected.Tag is FluentFTP.FtpListItem it)
            {
                if (it.Type == FluentFTP.FtpFileSystemObjectType.Directory)
                {
                    // đi vào thư mục
                    txtRemote.Text = it.FullName;
                    await RefreshListAsync(it.FullName);
                }
                else if (it.Type == FluentFTP.FtpFileSystemObjectType.File)
                {
                    // double click vào file -> download mặc định
                    var localFolder = txtLocal.Text;
                    Directory.CreateDirectory(localFolder);
                    var localPath = Path.Combine(localFolder, it.Name);
                    await DownloadFileAsync(it.FullName, localPath, deleteAfter: false);
                }
            }
        }

        private async Task DownloadFileAsync(string remoteFull, string localPath, bool deleteAfter)
        {
            // tạo _cts nếu chưa có (caller thông thường sẽ tạo trước, nhưng guard ở đây)
            if (_cts == null) _cts = new CancellationTokenSource();
            var token = _cts.Token;

            var pb = progressBar;
            try { pb.Value = 0; } catch { }

            SetUiBusy(true);
            var prevCursor = Cursor;
            Cursor = Cursors.WaitCursor;

            try
            {
                // NEW: throttle for debug logs
                var fileName = Path.GetFileName(localPath);
                DateTime lastLog = DateTime.MinValue;
                int lastPct = -1;

                var progress = new Progress<FluentFTP.FtpProgress>(p =>
                {
                    try
                    {
                        if (token.IsCancellationRequested) throw new OperationCanceledException(token);

                        var prog = (int)Math.Min(100, Math.Max(0, p.Progress));
                        if (pb.InvokeRequired)
                            pb.Invoke((Action)(() => pb.Value = prog));
                        else
                            pb.Value = prog;

                        long speed = 0;
                        try { speed = (long)p.TransferSpeed; } catch { speed = 0; }
                        string speedText = speed > 0 ? $"{FormatBytes(speed)}/s" : "--";
                        string eta = "--:--";
                        long estimatedTotalBytes = 0;
                        try
                        {
                            if (p.Progress > 0)
                            {
                                estimatedTotalBytes = (long)(p.TransferredBytes / (p.Progress / 100.0));
                                if (speed > 0)
                                {
                                    var remain = (double)(estimatedTotalBytes - p.TransferredBytes);
                                    var secs = remain / speed;
                                    eta = FormatSeconds(secs);
                                }
                            }
                        }
                        catch { eta = "--:--"; }

                        if (lblSpeed2 != null)
                        {
                            var text = $" {speedText}  ETA: {eta}  ({p.TransferredBytes}/{(estimatedTotalBytes > 0 ? estimatedTotalBytes.ToString() : "?")} bytes)";
                            if (lblSpeed2.InvokeRequired) lblSpeed2.Invoke((Action)(() => lblSpeed2.Text = text));
                            else lblSpeed2.Text = text;
                        }

                        // NEW: throttled debug log (every 10% or >= 2s)
                        var now = DateTime.UtcNow;
                        if (prog >= Math.Max(0, lastPct + 10) || (now - lastLog).TotalSeconds >= 2.0 || prog == 100)
                        {
                            Log($"File: {fileName}  {prog}%  ({p.TransferredBytes} bytes)  {speedText}  ETA {eta}");
                            lastPct = prog;
                            lastLog = now;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // don't rethrow here; cancellation will be handled outside
                    }
                    catch { }
                });

                Log($"Bắt đầu tải: {remoteFull} -> {localPath}");
                SetStatus($"Đang tải: {Path.GetFileName(localPath)} ...");

                // Try to find a DownloadFileAsync overload that accepts CancellationToken
                MethodInfo miDownloadWithToken = null;
                var methods = _client.GetType().GetMethods().Where(m => m.Name.IndexOf("DownloadFileAsync", StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                foreach (var m in methods)
                {
                    var ps = m.GetParameters();
                    if (ps.Any(p => p.ParameterType == typeof(CancellationToken)))
                    {
                        miDownloadWithToken = m;
                        break;
                    }
                }

                if (miDownloadWithToken != null)
                {
                    // build args: attempt to map common params: (localPath, remoteFull, FtpLocalExists, FtpVerify, progress, token)
                    var pars = miDownloadWithToken.GetParameters();
                    var args = new object[pars.Length];
                    for (int i = 0; i < pars.Length; i++)
                    {
                        var pn = pars[i].Name.ToLowerInvariant();
                        var pt = pars[i].ParameterType;
                        if (pt == typeof(string) && (pn.Contains("local") || pn.Contains("localpath") || pn.Contains("dest"))) args[i] = localPath;
                        else if (pt == typeof(string) && (pn.Contains("remote") || pn.Contains("remotepath") || pn.Contains("source"))) args[i] = remoteFull;
                        else if (pt == typeof(FluentFTP.FtpLocalExists)) args[i] = FluentFTP.FtpLocalExists.Overwrite;
                        else if (pt == typeof(FluentFTP.FtpVerify)) args[i] = FluentFTP.FtpVerify.None;
                        else if (pt == typeof(IProgress<FluentFTP.FtpProgress>)) args[i] = progress;
                        else if (pt == typeof(CancellationToken)) args[i] = token;
                        else args[i] = pt.IsValueType ? Activator.CreateInstance(pt) : null;
                    }

                    var ret = miDownloadWithToken.Invoke(_client, args);
                    if (ret is Task t) await t;
                }
                else
                {
                    // Fall back: call usual DownloadFileAsync and monitor cancellation by calling CancelAsync()
                    var miDefault = methods.FirstOrDefault(m => m.GetParameters().Length >= 2);
                    if (miDefault != null)
                    {
                        // prepare args for common signature (localPath, remoteFull, FtpLocalExists, FtpVerify, progress)
                        var ps = miDefault.GetParameters();
                        var args = new object[ps.Length];
                        for (int i = 0; i < ps.Length; i++)
                        {
                            var pn = ps[i].Name.ToLowerInvariant();
                            var pt = ps[i].ParameterType;
                            if (pt == typeof(string) && (pn.Contains("local") || pn.Contains("localpath") || pn.Contains("dest"))) args[i] = localPath;
                            else if (pt == typeof(string) && (pn.Contains("remote") || pn.Contains("remotepath") || pn.Contains("source"))) args[i] = remoteFull;
                            else if (pt == typeof(FluentFTP.FtpLocalExists)) args[i] = FluentFTP.FtpLocalExists.Overwrite;
                            else if (pt == typeof(FluentFTP.FtpVerify)) args[i] = FluentFTP.FtpVerify.None;
                            else if (pt == typeof(IProgress<FluentFTP.FtpProgress>)) args[i] = progress;
                            else args[i] = pt.IsValueType ? Activator.CreateInstance(pt) : null;
                        }

                        // Start download task
                        var downloadTask = (Task)miDefault.Invoke(_client, args);

                        // Monitor cancellation: when token requested, try to call CancelAsync on client
                        var monitoring = Task.Run(async () =>
                        {
                            try
                            {
                                await Task.Delay(-1, token); // wait until cancelled
                            }
                            catch (OperationCanceledException) { }
                            try
                            {
                                var miCancelAsync = _client.GetType().GetMethod("CancelAsync", Type.EmptyTypes);
                                if (miCancelAsync != null)
                                {
                                    var t = miCancelAsync.Invoke(_client, null) as Task;
                                }
                                else
                                {
                                    var miCancel = _client.GetType().GetMethod("Cancel", Type.EmptyTypes);
                                    miCancel?.Invoke(_client, null);
                                }
                            }
                            catch { }
                        });

                        // Wait for download or cancellation
                        var completed = await Task.WhenAny(downloadTask, monitoring);
                        if (completed == monitoring && token.IsCancellationRequested)
                        {
                            // ensure downloadTask is observed
                            try { await downloadTask; } catch { }
                            throw new OperationCanceledException(token);
                        }
                        else
                        {
                            // downloadTask finished
                            await downloadTask;
                        }
                    }
                    else
                    {
                        throw new NotSupportedException("Không tìm thấy phương thức DownloadFileAsync tương thích trên client.");
                    }
                }

                Log("Tải xuống hoàn tất: " + localPath);
                SetStatus($"Tải xong: {Path.GetFileName(localPath)}");

                if (deleteAfter)
                {
                    try
                    {
                        await _client.DeleteFileAsync(remoteFull);
                        Log("Đã xóa file trên FTP sau khi tải (move).");
                    }
                    catch (Exception ex)
                    {
                        Log("Xóa file trên FTP thất bại: " + ex.Message);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Log("Tải bị huỷ bởi người dùng: " + remoteFull);
                SetStatus("Tải bị hủy bởi người dùng.", true);
                try { if (File.Exists(localPath)) File.Delete(localPath); } catch { }
                throw;
            }
            catch (Exception ex)
            {
                Log("Lỗi tải: " + ex.Message);
                MessageBox.Show("Lỗi tải: " + ex.Message);
                SetStatus("Lỗi khi tải: " + ex.Message, true);
            }
            finally
            {
                try { pb.Value = 0; } catch { }
                try
                {
                    if (lblSpeed2 != null)
                    {
                        if (lblSpeed2.InvokeRequired) lblSpeed2.Invoke((Action)(() => lblSpeed2.Text = ""));
                        else lblSpeed2.Text = "";
                    }
                }
                catch { }

                Cursor = prevCursor;
                SetUiBusy(false);
            }
        }

        //ngắt kết nối khi đóng form
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            try { _client?.Dispose(); } catch { }
        }

        private async Task RefreshListAsync(string remotePath)
        {
            if (_client == null || !_client.IsConnected)
            {
                MessageBox.Show("Chưa kết nối tới FTP.");
                return;
            }

            try
            {
                var items = await _client.GetListingAsync(remotePath);

                // log số mục trả về
                Log($"Server trả về {items?.Length ?? 0} mục cho '{remotePath}'.");

                Action updateAction = () =>
                {
                    lvFiles.BeginUpdate();
                    try
                    {
                        lvFiles.Items.Clear();

                        // Thêm Folder trước, File sau (tuỳ thích)
                        var dirs = items.Where(it => it.Type == FluentFTP.FtpFileSystemObjectType.Directory);
                        var files = items.Where(it => it.Type == FluentFTP.FtpFileSystemObjectType.File);

                        foreach (var it in dirs)
                        {
                            var lvi = new ListViewItem(it.Name);
                            lvi.SubItems.Add(""); // kích thước để trống cho folder
                            lvi.SubItems.Add(it.Modified.ToString("yyyy-MM-dd HH:mm:ss"));
                            lvi.Tag = it; // lưu FtpListItem để dùng khi double click
                            lvi.ImageKey = "folder";
                            lvFiles.Items.Add(lvi);
                        }

                        foreach (var it in files)
                        {
                            var lvi = new ListViewItem(it.Name);
                            lvi.SubItems.Add(it.Size.ToString());
                            lvi.SubItems.Add(it.Modified.ToString("yyyy-MM-dd HH:mm:ss"));
                            lvi.Tag = it;
                            lvi.ImageKey = "file";
                            lvFiles.Items.Add(lvi);
                        }
                    }
                    finally
                    {
                        lvFiles.EndUpdate();
                    }

                    //Log($"Đã thêm vào lvFiles: {lvFiles.Items.Count} mục.");
                };

                if (lvFiles.InvokeRequired) lvFiles.Invoke(updateAction); else updateAction();
            }
            catch (Exception ex)
            {
                Log("Liệt kê thất bại: " + ex.Message);
                MessageBox.Show("Liệt kê thất bại: " + ex.Message);
            }
        }

        private async void BtnUp_Click(object sender, EventArgs e)
        {
            var current = txtRemote.Text.Trim();
            var parent = GetFtpParent(current);
            txtRemote.Text = parent;
            await RefreshListAsync(parent);
        }

        // Helper: chuẩn hoá dạng đường dẫn FTP
        private string NormalizeFtpPath(string p)
        {
            if (string.IsNullOrEmpty(p)) return p;
            p = p.Replace('\\', '/');
            while (p.Contains("//")) p = p.Replace("//", "/");
            if (!p.StartsWith("/")) p = "/" + p;
            return p;
        }

        // Lấy parent path kiểu FTP
        private string GetFtpParent(string path)
        {
            if (string.IsNullOrEmpty(path) || path == "/") return "/";
            path = path.TrimEnd('/');
            var idx = path.LastIndexOf('/');
            if (idx <= 0) return "/";
            return path.Substring(0, idx);
        }

        // Thử nhiều biến thể để tìm đường dẫn file tồn tại trên server
        private async Task<string> FindExistingRemoteFileAsync(string candidate, string currentFolder, string fileName)
        {
            currentFolder = string.IsNullOrEmpty(currentFolder) ? "/" : currentFolder;
            currentFolder = currentFolder.Replace('\\', '/').TrimEnd('/');
            if (currentFolder == "") currentFolder = "/";

            // build variants to try
            var tries = new List<string>();
            if (!string.IsNullOrEmpty(candidate)) tries.Add(candidate);
            if (!string.IsNullOrEmpty(candidate) && candidate.StartsWith("/")) tries.Add(candidate.TrimStart('/'));
            if (!string.IsNullOrEmpty(fileName)) tries.Add(fileName);
            tries.Add((currentFolder == "/" ? "" : currentFolder) + "/" + fileName);
            tries.Add("/" + fileName);
            tries = tries.Select(t => NormalizeFtpPath(t)).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct().ToList();

            //Log("Thử các biến thể đường dẫn: " + string.Join(", ", tries));

            // Thử tìm bằng GetListing trên từng parent folder
            var parentFolders = tries.Select(t => GetFtpParent(t)).Distinct().ToList();

            foreach (var parent in parentFolders)
            {
                try
                {
                    // gọi GetListingAsync nếu có, fallback GetListing
                    object listingObj = null;
                    var miListAsync = _client.GetType().GetMethod("GetListingAsync", new Type[] { typeof(string) });
                    if (miListAsync != null)
                    {
                        var task = (Task)miListAsync.Invoke(_client, new object[] { parent });
                        await task;
                        var resultProp = task.GetType().GetProperty("Result");
                        if (resultProp != null) listingObj = resultProp.GetValue(task);
                    }
                    else
                    {
                        var miList = _client.GetType().GetMethod("GetListing", new Type[] { typeof(string) });
                        if (miList != null)
                        {
                            listingObj = miList.Invoke(_client, new object[] { parent });
                        }
                    }

                    if (listingObj is System.Collections.IEnumerable en)
                    {
                        foreach (var it in en)
                        {
                            var nameProp = it.GetType().GetProperty("Name");
                            var fullProp = it.GetType().GetProperty("FullName");
                            var name = nameProp?.GetValue(it)?.ToString();
                            var full = fullProp?.GetValue(it)?.ToString();

                            if (string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase) ||
                                (!string.IsNullOrEmpty(full) && full.EndsWith("/" + fileName, StringComparison.OrdinalIgnoreCase)))
                            {
                                // trả về đường dẫn hợp lệ (ưu tiên full nếu có)
                                var found = !string.IsNullOrEmpty(full) ? NormalizeFtpPath(full)
                                    : (parent == "/" ? "/" + fileName : parent + "/" + fileName);
                                //Log("Dò thấy file qua listing: " + found);
                                return found;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    //Log($"Lỗi khi GetListing trên '{parent}': {ex.Message}");
                }
            }

            // Nếu vẫn chưa tìm thấy, thử các biến thể trực tiếp với GetListing trên currentFolder
            try
            {
                var miListA = _client.GetType().GetMethod("GetListingAsync", new Type[] { typeof(string) });
                object listingObj = null;
                if (miListA != null)
                {
                    var task = (Task)miListA.Invoke(_client, new object[] { currentFolder });
                    await task;
                    var resultProp = task.GetType().GetProperty("Result");
                    if (resultProp != null) listingObj = resultProp.GetValue(task);
                }
                else
                {
                    var miList = _client.GetType().GetMethod("GetListing", new Type[] { typeof(string) });
                    if (miList != null) listingObj = miList.Invoke(_client, new object[] { currentFolder });
                }

                if (listingObj is System.Collections.IEnumerable en)
                {
                    foreach (var it in en)
                    {
                        var nameProp = it.GetType().GetProperty("Name");
                        var fullProp = it.GetType().GetProperty("FullName");
                        var name = nameProp?.GetValue(it)?.ToString();
                        var full = fullProp?.GetValue(it)?.ToString();
                        if (string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase))
                        {
                            var found = !string.IsNullOrEmpty(full) ? NormalizeFtpPath(full) : (currentFolder == "/" ? "/" + fileName : currentFolder + "/" + fileName);
                            //Log("Tìm thấy file trong current folder: " + found);
                            return found;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                //Log("Lỗi khi dò current folder: " + ex.Message);
            }

            return null;
        }

        private void SetUiBusy(bool busy)
        {
            btnDownload.Enabled = !busy;
            //btnMove.Enabled = !busy;
            //btnHash.Enabled = !busy;
            btnList.Enabled = !busy;
            btnConnect.Enabled = !busy;
            btnBrowseLocal.Enabled = !busy;
            lvFiles.Enabled = !busy;
            txtRemote.Enabled = !busy;
            txtLocal.Enabled = !busy;

            // quản lý cancel
            if (btnCancel != null)
            {
                btnCancel.Enabled = busy;
                btnCancel.Visible = busy;
            }

            // show/ẩn progressBar
            progressBar.Visible = busy ? true : progressBar.Visible;
        }

        private async Task DownloadDirectoryAsync(string remoteDir, string localDir, bool deleteAfter)
        {
            if (_cts == null) _cts = new CancellationTokenSource();
            var token = _cts.Token;

            try
            {
                Log($"Tải thư mục: {remoteDir} -> {localDir}");
                Directory.CreateDirectory(localDir);

                object listingObj = null;
                var miListAsync = _client.GetType().GetMethod("GetListingAsync", new Type[] { typeof(string) });
                if (miListAsync != null)
                {
                    var task = (Task)miListAsync.Invoke(_client, new object[] { remoteDir });
                    await task;
                    var rp = task.GetType().GetProperty("Result");
                    if (rp != null) listingObj = rp.GetValue(task);
                }
                else
                {
                    var miList = _client.GetType().GetMethod("GetListing", new Type[] { typeof(string) });
                    if (miList != null) listingObj = miList.Invoke(_client, new object[] { remoteDir });
                }

                if (!(listingObj is System.Collections.IEnumerable entries))
                {
                    Log($"Không lấy được listing cho '{remoteDir}'");
                    return;
                }

                foreach (var it in entries)
                {
                    if (token.IsCancellationRequested) throw new OperationCanceledException(token);

                    try
                    {
                        var info = InspectListingItem(it);
                        string rawName = info.Name;
                        string rawFull = info.FullName;

                        string name = SanitizeRemoteName(rawName ?? "");
                        if (string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(rawFull))
                            name = SanitizeRemoteName(Path.GetFileName(rawFull));

                        if (string.IsNullOrEmpty(name))
                        {
                            Log("Bỏ qua mục không có tên trong listing (ẩn).");
                            continue;
                        }

                        string childRemote = !string.IsNullOrEmpty(rawFull) ? rawFull.Trim() : (remoteDir.TrimEnd('/') + "/" + name);
                        childRemote = childRemote.Replace('\\', '/');

                        string childLocal = Path.Combine(localDir, name);

                        bool isDir = await DecideIsDirectoryAsync(it, childRemote);

                        if (isDir)
                        {
                            Directory.CreateDirectory(childLocal);
                            await DownloadDirectoryAsync(childRemote, childLocal, deleteAfter);
                        }
                        else
                        {
                            Log($"-> Tải file: remote='{childRemote}' -> local='{childLocal}'");
                            Directory.CreateDirectory(Path.GetDirectoryName(childLocal) ?? localDir);

                            // respect cancellation in file download
                            await DownloadFileAsync(childRemote, childLocal, deleteAfter);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        Log("Đã hủy tải thư mục: " + remoteDir);
                        throw;
                    }
                    catch (Exception exIt)
                    {
                        Log($"Lỗi khi xử lý entry trong '{remoteDir}': {exIt.Message}");
                    }
                }

                if (deleteAfter)
                {
                    try
                    {
                        var miDelDirAsync = _client.GetType().GetMethod("DeleteDirectoryAsync", new Type[] { typeof(string) });
                        if (miDelDirAsync != null)
                        {
                            var t = (Task)miDelDirAsync.Invoke(_client, new object[] { remoteDir });
                            await t;
                        }
                        else
                        {
                            var miDelDir = _client.GetType().GetMethod("DeleteDirectory", new Type[] { typeof(string) });
                            if (miDelDir != null) miDelDir.Invoke(_client, new object[] { remoteDir });
                        }
                        Log($"Đã xóa thư mục remote (nếu được): {remoteDir}");
                    }
                    catch (Exception ex)
                    {
                        Log($"Không xóa được thư mục remote '{remoteDir}': {ex.Message}");
                    }
                }
            }
            finally
            {

            }
        }

        private async Task DownloadSelectedItemsAsync(bool deleteAfter)
        {
            if (lvFiles.SelectedItems.Count == 0)
            {
                MessageBox.Show("Chọn ít nhất một mục để tải.");
                return;
            }

            // create new CTS for this whole operation
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            SetUiBusy(true);
            var prevCursor = Cursor;
            Cursor = Cursors.WaitCursor;

            try
            {
                var selected = lvFiles.SelectedItems.Cast<ListViewItem>().ToArray();
                var currentRemote = txtRemote.Text.Trim();
                var localBase = txtLocal.Text.Trim();
                if (string.IsNullOrEmpty(localBase)) localBase = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                Directory.CreateDirectory(localBase);

                // 1) Plan all files and total size
                SetStatus("Đang đọc danh sách tệp để tải...");
                var plan = await PlanDownloadsFromSelectionAsync(selected, currentRemote, localBase, token);
                var knownTotal = plan.Where(x => x.size.HasValue).Sum(x => x.size!.Value);
                Log($"Planned {plan.Count} file(s) to download. Total known bytes = {knownTotal}.");

                var overall = new OverallTransfer(this, knownTotal, plan.Count);

                // 2) Execute downloads with overall progress
                int done = 0;
                foreach (var item in plan)
                {
                    token.ThrowIfCancellationRequested();

                    var dir = Path.GetDirectoryName(item.local) ?? localBase;
                    Directory.CreateDirectory(dir);

                    SetStatus($"Đang tải ({++done}/{plan.Count}): {Path.GetFileName(item.local)}");
                    Log($"Download: {item.remote} -> {item.local}");

                    var perFile = overall.CreatePerFileProgress(item.remote, item.size);

                    var ok = await DownloadRemoteFileSafeAsync(item.remote, item.local, token, perFile);
                    if (!ok)
                    {
                        Log($"Tải file thất bại: {item.remote}");
                        continue;
                    }

                    if (deleteAfter)
                    {
                        try { await _client.DeleteFileAsync(item.remote); } catch (Exception exDel) { Log("Delete remote failed: " + exDel.Message); }
                    }
                }

                if (deleteAfter)
                {
                    try { await RefreshListAsync(string.IsNullOrEmpty(txtRemote.Text.Trim()) ? "/" : txtRemote.Text.Trim()); } catch { }
                }

                SetStatus("Tải hoàn tất.");
            }
            catch (OperationCanceledException)
            {
                MessageBox.Show("Đã hủy tải theo yêu cầu.", "Đã hủy", MessageBoxButtons.OK, MessageBoxIcon.Information);
                SetStatus("Đã hủy.", true);
            }
            finally
            {
                SetUiBusy(false);
                Cursor = prevCursor;
                try { progressBar.Value = 0; } catch { }
                if (_cts != null)
                {
                    try { _cts.Dispose(); } catch { }
                    _cts = null;
                }
            }
        }
        // Helper: format bytes -> human readable
        private string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            double kb = bytes / 1024.0;
            if (kb < 1024) return $"{kb:F1} KB";
            double mb = kb / 1024.0;
            if (mb < 1024) return $"{mb:F2} MB";
            double gb = mb / 1024.0;
            return $"{gb:F2} GB";
        }

        // Helper: format seconds -> hh:mm:ss
        private string FormatSeconds(double seconds)
        {
            if (double.IsInfinity(seconds) || double.IsNaN(seconds) || seconds < 0) return "--:--";
            var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
            if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
            return $"{ts.Minutes:D2}:{ts.Seconds:D2}";
        }

        private async Task<bool> IsRemoteDirectoryAsync(string remotePath)
        {
            if (string.IsNullOrEmpty(remotePath)) return false;
            remotePath = remotePath.Replace('\\', '/').Trim();

            try
            {
                // 1) Thử gọi DirectoryExistsAsync / DirectoryExists nếu có
                var miDirAsync = _client.GetType().GetMethod("DirectoryExistsAsync", new Type[] { typeof(string) });
                if (miDirAsync != null)
                {
                    var task = (Task)miDirAsync.Invoke(_client, new object[] { remotePath });
                    await task;
                    var resultProp = task.GetType().GetProperty("Result");
                    if (resultProp != null) return (bool)resultProp.GetValue(task);
                }
                else
                {
                    var miDir = _client.GetType().GetMethod("DirectoryExists", new Type[] { typeof(string) });
                    if (miDir != null)
                    {
                        return (bool)miDir.Invoke(_client, new object[] { remotePath });
                    }
                }
            }
            catch { /* ignore and fallback to listing */ }

            try
            {
                // 2) Fallback: lấy listing của parent và tìm mục có tên tương ứng, so sánh Type
                var parent = GetFtpParent(remotePath);
                var name = remotePath.TrimEnd('/').Split('/').Last();

                object listingObj = null;
                var miListAsync = _client.GetType().GetMethod("GetListingAsync", new Type[] { typeof(string) });
                if (miListAsync != null)
                {
                    var task = (Task)miListAsync.Invoke(_client, new object[] { parent });
                    await task;
                    var resultProp = task.GetType().GetProperty("Result");
                    if (resultProp != null) listingObj = resultProp.GetValue(task);
                }
                else
                {
                    var miList = _client.GetType().GetMethod("GetListing", new Type[] { typeof(string) });
                    if (miList != null) listingObj = miList.Invoke(_client, new object[] { parent });
                }

                if (listingObj is System.Collections.IEnumerable en)
                {
                    foreach (var it in en)
                    {
                        var nameProp = it.GetType().GetProperty("Name");
                        var fullProp = it.GetType().GetProperty("FullName");
                        var typeProp = it.GetType().GetProperty("Type");

                        var itemName = nameProp?.GetValue(it)?.ToString()?.Trim();
                        var itemFull = fullProp?.GetValue(it)?.ToString()?.Trim();
                        var itemType = typeProp?.GetValue(it);

                        if (string.IsNullOrEmpty(itemName) && !string.IsNullOrEmpty(itemFull))
                            itemName = Path.GetFileName(itemFull);

                        if (string.Equals(itemName, name, StringComparison.OrdinalIgnoreCase) ||
                            (!string.IsNullOrEmpty(itemFull) && itemFull.EndsWith("/" + name, StringComparison.OrdinalIgnoreCase)))
                        {
                            // Nếu có property Type, kiểm tra xem có phải Directory
                            if (itemType != null)
                            {
                                var tstr = itemType.ToString();
                                if (tstr.IndexOf("Directory", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    tstr.IndexOf("Folder", StringComparison.OrdinalIgnoreCase) >= 0)
                                    return true;
                                else
                                    return false;
                            }
                            // nếu không có info Type, thử dùng FullName có slash cuối
                            if (!string.IsNullOrEmpty(itemFull) && itemFull.EndsWith("/")) return true;
                        }
                    }
                }
            }
            catch { /* ignore */ }

            // 3) Không xác định -> mặc định coi là file
            return false;
        }

        // ---- Helper: sanitize tên file (loại CR/LF và trim) ----
        private string SanitizeRemoteName(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            // loại CR/LF, control chars, trim spaces
            var s = name.Replace("\r", "").Replace("\n", "").Trim();
            // thay những ký tự không hợp lệ trên Windows bằng '_'
            foreach (var c in Path.GetInvalidFileNameChars())
            {
                s = s.Replace(c, '_');
            }
            return s;
        }

        // Helper: inspect item object và return a tuple of common props (for logging/debug)
        private (string Name, string FullName, string Type, long? Size, string Modified) InspectListingItem(object it)
        {
            string name = null, full = null, type = null, mod = null;
            long? size = null;
            try
            {
                var nameProp = it.GetType().GetProperty("Name");
                var fullProp = it.GetType().GetProperty("FullName");
                var typeProp = it.GetType().GetProperty("Type");
                var sizeProp = it.GetType().GetProperty("Size");
                var modProp = it.GetType().GetProperty("Modified");

                name = nameProp?.GetValue(it)?.ToString();
                full = fullProp?.GetValue(it)?.ToString();
                type = typeProp?.GetValue(it)?.ToString();
                mod = modProp?.GetValue(it)?.ToString();
                if (sizeProp != null)
                {
                    var sval = sizeProp.GetValue(it);
                    if (sval != null)
                    {
                        try { size = Convert.ToInt64(sval); } catch { size = null; }
                    }
                }
            }
            catch { /* ignore */ }

            return (name, full, type, size, mod);
        }

        // Quyết định isDir an toàn
        private async Task<bool> DecideIsDirectoryAsync(object listingItem, string candidateRemote)
        {
            // 1) Inspect object
            var info = InspectListingItem(listingItem);
            string name = info.Name?.Trim();
            string full = info.FullName?.Trim();
            string type = info.Type;
            long? size = info.Size;

            Log($"Inspect item: Name='{name}', FullName='{full}', Type='{type}', Size={(size.HasValue ? size.Value.ToString() : "null")}, Modified='{info.Modified}'");

            // 2) If Type clearly indicates, use it
            if (!string.IsNullOrEmpty(type))
            {
                if (type.IndexOf("Directory", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    type.IndexOf("Folder", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (type.IndexOf("File", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            }

            // 3) If we have Size property -> treat as file 
            if (size.HasValue)
            {
                // even size==0, more likely a file than a directory
                return false;
            }

            // 4) If full endsWith '/' -> directory
            if (!string.IsNullOrEmpty(full) && full.EndsWith("/")) return true;

            // 5) Heuristic: if name has an extension-like dot -> usually file,
            //    but skip when it looks like a pure numeric version (e.g. "1.0.0").
            if (!string.IsNullOrEmpty(name) && name.Contains("."))
            {
                var parts = name.Split('.');
                if (parts.Length >= 2 && parts.Last().Length <= 8) // extension length limit
                {
                    // treat as file only when the last segment is NOT purely numeric
                    var last = parts.Last();
                    var isLastNumeric = System.Text.RegularExpressions.Regex.IsMatch(last, @"^\d+$");
                    if (!isLastNumeric)
                    {
                        // common filename.extension case (e.g. "readme.txt") -> file
                        return false;
                    }
                    // else: looks like "1.0.0" (all numeric) -> fall through and continue safer checks
                }
            }

            // 6) Attempt a direct LIST on the path itself. Many servers return directory contents for LIST <dir>.
            //    If LIST returns multiple entries -> directory. If it returns a single entry, inspect that entry.
            if (!string.IsNullOrEmpty(candidateRemote))
            {
                try
                {
                    var listing = (await GetRemoteListingAsync(candidateRemote))?.ToList() ?? new List<object>();
                    if (listing.Count > 1)
                    {
                        Log($"GetListing on '{candidateRemote}' returned {listing.Count} entries -> treat as directory.");
                        return true;
                    }
                    else if (listing.Count == 1)
                    {
                        var it = listing[0];
                        try
                        {
                            var typeProp = it.GetType().GetProperty("Type");
                            var tval = typeProp?.GetValue(it)?.ToString();
                            if (!string.IsNullOrEmpty(tval))
                            {
                                if (tval.IndexOf("Directory", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    tval.IndexOf("Folder", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                                if (tval.IndexOf("File", StringComparison.OrdinalIgnoreCase) >= 0) return false;
                            }

                            var nameProp = it.GetType().GetProperty("Name");
                            var sizeProp = it.GetType().GetProperty("Size");
                            var nm = nameProp?.GetValue(it)?.ToString();
                            var sval = sizeProp?.GetValue(it);
                            if (!string.IsNullOrEmpty(nm) && string.Equals(nm, Path.GetFileName(candidateRemote), StringComparison.OrdinalIgnoreCase))
                            {
                                if (sval != null) return false; // listed same name and has size -> file
                            }

                            // otherwise conservatively treat as directory (LIST succeeded)
                            Log($"GetListing on '{candidateRemote}' ambiguous -> treating as directory by default.");
                            return true;
                        }
                        catch
                        {
                            // if parsing the single item fails, treat conservatively as directory
                            Log($"Error inspecting listing item for '{candidateRemote}', treat as directory.");
                            return true;
                        }
                    }
                    // listing.Count == 0 -> could be empty dir or server returns empty; fall through to other checks
                }
                catch (Exception ex)
                {
                    Log($"GetListing direct check for '{candidateRemote}' failed: {ex.Message}");
                    // fall back to other checks below
                }
            }

            // 7) Fallback: try IsRemoteDirectoryAsync (checks DirectoryExists or parent listing)
            try
            {
                if (!string.IsNullOrEmpty(candidateRemote))
                {
                    bool r = await IsRemoteDirectoryAsync(candidateRemote);
                    Log($"Fallback IsRemoteDirectoryAsync('{candidateRemote}') => {r}");
                    return r;
                }
            }
            catch (Exception ex)
            {
                Log("Fallback IsRemoteDirectoryAsync lỗi: " + ex.Message);
            }

            // Default: file (keeps previous behavior when ambiguous)
            return false;
        }

        // Handler cho nút Cancel 
        private void BtnCancel_Click(object sender, EventArgs e)
        {
            if (_cts == null)
            {
                Log("Không có tiến trình để hủy.");
                return;
            }

            if (!_cts.IsCancellationRequested)
            {
                Log("Yêu cầu hủy tải...");
                try { _cts.Cancel(); } catch { }

                // Nếu fluentftp hỗ trợ CancelAsync/Cancel thì gọi để ép ngắt ngay
                try
                {
                    var miCancelAsync = _client?.GetType().GetMethod("CancelAsync", Type.EmptyTypes);
                    if (miCancelAsync != null)
                    {
                        var t = miCancelAsync.Invoke(_client, null) as Task;
                        // không await ở đây để tránh deadlock; chỉ fire-and-forget
                    }
                    else
                    {
                        var miCancel = _client?.GetType().GetMethod("Cancel", Type.EmptyTypes);
                        miCancel?.Invoke(_client, null);
                    }
                }
                catch { /* ignore */ }
            }
            else
            {
                Log("Đã yêu cầu hủy trước đó.");
            }
        }

        /// <summary>
        /// Cập nhật dòng trạng thái ở bottom. isError=true sẽ đổi màu chữ đỏ.
        /// Thread-safe: có thể gọi từ background thread.
        /// </summary>
        private void SetStatus(string text, bool isError = false)
        {
            try
            {
                if (this.InvokeRequired)
                {
                    this.Invoke((Action)(() => SetStatus(text, isError)));
                    return;
                }

                if (toolStripStatusLabelStatus != null)
                {
                    toolStripStatusLabelStatus.Text = text ?? "";
                    toolStripStatusLabelStatus.ForeColor = isError ? Color.Red : SystemColors.ControlText;
                }

                // Log("STATUS: " + text);
            }
            catch { /* ignore UI thread errors */ }
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            SetStatus("Chưa kết nối");

            // nếu dùng btnCancel mặc định disable:
            if (btnCancel != null) btnCancel.Enabled = false;
            LoadCredentialsAtStartup();

            // gọi non-blocking kiểm tra update (chờ 2s để UI sẵn sàng)
            _ = Task.Run(async () =>
            {

                try
                {
                    // Delay ngắn để tránh bùng nổ network ngay khi app mở
                    await Task.Delay(TimeSpan.FromSeconds(2));

                    // Lấy feed URL từ assembly metadata (theo hướng dẫn trước)
                    var feed = GetUpdateFeedUrlFromAssembly();

                    // Nếu không set trong assembly, bạn có thể đọc từ settings hoặc textbox
                    if (string.IsNullOrWhiteSpace(feed))
                    {
                        // ví dụ: feed = txtUpdateManifestUrl?.Text?.Trim();
                        Log("Không tìm thấy UpdateFeedUrl trong assembly - bỏ qua kiểm tra update.");
                        return;
                    }

                    //Log("Tự động kiểm tra cập nhật từ: " + feed);

                    // Gọi hàm kiểm tra & prompt. Dùng Try/Catch để không nổ UI
                    // Lưu ý: CheckAndUpdate... có thể hiển thị MessageBox -> đó chạy trên background thread,
                    // nên chúng ta marshal về UI thread khi cần. Nếu hàm của bạn đã xử lý UI-safe, gọi trực tiếp.
                    //await CheckAndUpdateViaInnoAsync(feed); // hoặc CheckForUpdatesAndPromptAsync(feed)
                }
                catch (Exception ex)
                {
                    // Không cho exception phá Form_Load
                    Log("Lỗi khi kiểm tra cập nhật (auto): " + ex.Message);
                }

            });
            SetStatus("Chưa kết nối");
        }


        private void UpdateConnectionStatusLabel()
        {
            try
            {
                if (this.InvokeRequired) { this.Invoke((Action)UpdateConnectionStatusLabel); return; }
                if (_client != null)
                    SetStatus(_client.IsConnected ? "Đã kết nối" : "Chưa kết nối");
                else
                    SetStatus("Chưa kết nối");
            }
            catch { }
        }

        private string GetLocalVersionFromAssembly()
        {
            try
            {
                var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
                // prefer informational version if set
                var infoVer = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                if (!string.IsNullOrEmpty(infoVer)) return infoVer;
                var fileVer = FileVersionInfo.GetVersionInfo(asm.Location).FileVersion;
                if (!string.IsNullOrEmpty(fileVer)) return fileVer;
                var ver = asm.GetName().Version?.ToString();
                return ver ?? "0.0.0";
            }
            catch { return "0.0.0"; }
        }

        // fetch manifest
        private async Task<(string version, string url)> FetchUpdateManifestAsync(string manifestUrl)
        {
            using (var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
            {
                var txt = await hc.GetStringAsync(manifestUrl);
                var doc = JsonSerializer.Deserialize<JsonElement>(txt);
                var v = doc.GetProperty("version").GetString();
                var u = doc.GetProperty("url").GetString();
                return (v, u);
            }
        }

        // compare simple semver (reuse earlier CompareSemver from previous messages)
        private int CompareSemverVersions(string a, string b)
        {
            // returns: -1 if v1 < v2, 0 equal, 1 if v1 > v2
            Version TryParseVer(string s)
            {
                if (string.IsNullOrEmpty(s)) return new Version(0, 0, 0);
                // remove prefix like 'v'
                s = s.Trim();
                if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase)) s = s.Substring(1);
                // ensure at least 3 components
                var parts = s.Split(new[] { '.', '-' }, StringSplitOptions.RemoveEmptyEntries);
                while (parts.Length < 3) Array.Resize(ref parts, parts.Length + 1);
                for (int i = 0; i < parts.Length; i++) if (parts[i] == null) parts[i] = "0";
                try
                {
                    var p0 = parts.Length > 0 && int.TryParse(parts[0], out int a0) ? a0 : 0;
                    var p1 = parts.Length > 1 && int.TryParse(parts[1], out int a1) ? a1 : 0;
                    var p2 = parts.Length > 2 && int.TryParse(parts[2], out int a2) ? a2 : 0;
                    return new Version(p0, p1, p2);
                }
                catch { return new Version(0, 0, 0); }
            }

            var A = TryParseVer(a);
            var B = TryParseVer(b);
            return A.CompareTo(B);
        }

        // download installer with progress
        private async Task<string> DownloadInstallerAsync(string url, IProgress<double> progress = null, CancellationToken token = default)
        {
            var dest = Path.Combine(Path.GetTempPath(), Path.GetFileName(new Uri(url).LocalPath));
            if (File.Exists(dest)) File.Delete(dest);
            using (var hc = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
            using (var resp = await hc.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token))
            {
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength ?? -1L;
                using (var stream = await resp.Content.ReadAsStreamAsync(token))
                using (var fs = new FileStream(dest, FileMode.CreateNew))
                {
                    var buffer = new byte[81920];
                    long read = 0;
                    while (true)
                    {
                        token.ThrowIfCancellationRequested();
                        var n = await stream.ReadAsync(buffer, 0, buffer.Length, token);
                        if (n == 0) break;
                        await fs.WriteAsync(buffer, 0, n, token);
                        read += n;
                        if (total > 0) progress?.Report(read * 1.0 / total);
                    }
                }
            }
            return dest;
        }

        // run installer (elevated) and exit app
        private void RunInstallerAndExit(string installerPath, bool silent)
        {
            var args = silent ? "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART" : "";
            var psi = new ProcessStartInfo(installerPath)
            {
                UseShellExecute = true,
                Verb = "runas", // prompt for elevation
                Arguments = args
            };
            Process.Start(psi);
            Application.Exit(); // allow installer to replace files
        }

        // Combined flow
        private async Task CheckAndUpdateViaInnoAsync(string manifestUrl)
        {
            SetStatus("Kiểm tra cập nhật...");
            var (remoteVer, url) = await FetchUpdateManifestAsync(manifestUrl);
            var localVer = GetLocalVersionFromAssembly();
            if (CompareSemverVersions(localVer, remoteVer) >= 0) { SetStatus("Đã là bản mới nhất."); return; }

            var r = MessageBox.Show($"Có bản mới {remoteVer}. Tải và cài không?", "Cập nhật", MessageBoxButtons.YesNo);
            if (r != DialogResult.Yes) return;

            SetStatus("Đang tải installer...");
            var cts = new CancellationTokenSource();
            var installer = await DownloadInstallerAsync(url, new Progress<double>(p =>
            {
                progressBar.Value = (int)(p * 100);
                lblSpeed2.Text = $"{(int)(p * 100)}%";
            }), cts.Token);

            if (installer == null) { SetStatus("Tải thất bại", true); return; }

            SetStatus("Khởi chạy installer...");
            RunInstallerAndExit(installer, silent: true);
        }

        private string GetUpdateFeedUrlFromAssembly()
        {
            try
            {
                var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
                var meta = asm.GetCustomAttributes()
                              .OfType<AssemblyMetadataAttribute>()
                              .FirstOrDefault(a => string.Equals(a.Key, "UpdateFeedUrl", StringComparison.OrdinalIgnoreCase));
                return meta?.Value;
            }
            catch
            {
                return null;
            }
        }


        // Parse plain text key=value file
        private FtpSettings ReadFtpSettingsFromPlainFile(string path)
        {
            var s = new FtpSettings();
            if (!File.Exists(path)) return null;

            foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                var line = raw?.Trim();
                if (string.IsNullOrEmpty(line)) continue;
                if (line.StartsWith("#")) continue;
                var idx = line.IndexOf('=');
                if (idx < 0) continue;
                var key = line.Substring(0, idx).Trim();
                var val = line.Substring(idx + 1).Trim();

                switch (key.ToLowerInvariant())
                {
                    case "host": s.Host = val; break;
                    case "port":
                        if (int.TryParse(val, out int p)) s.Port = p;
                        break;
                    case "user": s.User = val; break;
                    case "password": s.Password = val; break;
                    case "remote": s.Remote = val; break;
                    case "encryption": s.Encryption = val; break;
                }
            }

            // basic validation
            if (string.IsNullOrWhiteSpace(s.Host) || string.IsNullOrWhiteSpace(s.User)) return null;
            return s;
        }

        // DPAPI: decrypt a base64 blob previously protected by ProtectAndSaveEncryptedFile
        private FtpSettings ReadFtpSettingsFromDpapiFile(string path)
        {
            if (!File.Exists(path)) return null;
            try
            {
                var b64 = File.ReadAllText(path, Encoding.UTF8);
                var blob = Convert.FromBase64String(b64);
                // Protect with CurrentUser so only current Windows user can decrypt
                var plain = ProtectedData.Unprotect(blob, null, DataProtectionScope.CurrentUser);
                var txt = Encoding.UTF8.GetString(plain);
                // txt expected same format as plain key=value lines
                var tmpFile = Path.GetTempFileName();
                File.WriteAllText(tmpFile, txt, Encoding.UTF8);
                var settings = ReadFtpSettingsFromPlainFile(tmpFile);
                try { File.Delete(tmpFile); } catch { }
                return settings;
            }
            catch
            {
                return null;
            }
        }

        // Helper: create encrypted DPAPI file from plain settings (use once to generate file)
        private void CreateDpapiEncryptedSettingsFile(string outputPath, FtpSettings settings)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"host={settings.Host}");
            sb.AppendLine($"port={settings.Port}");
            sb.AppendLine($"user={settings.User}");
            sb.AppendLine($"password={settings.Password}");
            sb.AppendLine($"remote={settings.Remote}");
            sb.AppendLine($"encryption=dpapi");
            var txt = sb.ToString();
            var plain = Encoding.UTF8.GetBytes(txt);
            var protectedBytes = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            var b64 = Convert.ToBase64String(protectedBytes);
            // ensure directory exists
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
            File.WriteAllText(outputPath, b64, Encoding.UTF8);
            // set file as hidden (optional)
            try { File.SetAttributes(outputPath, FileAttributes.Hidden); } catch { }
        }

        // High level loader: auto-detect file type based on `encryption=` or file content
        private FtpSettings LoadFtpSettingsFromFile(string path)
        {
            if (!File.Exists(path)) return null;

            // First try to read as plain text and detect "encryption=dpapi" line
            try
            {
                var firstLines = File.ReadLines(path).Take(10).ToArray();
                var joined = string.Join("\n", firstLines);
                if (joined.IndexOf("encryption=dpapi", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    // It's plain file with encryption tag (unlikely) — but try plain parse first
                    var plain = ReadFtpSettingsFromPlainFile(path);
                    if (plain != null && string.Equals(plain.Encryption, "dpapi", StringComparison.OrdinalIgnoreCase))
                    {
                        // user wrote encryption but file is plain (unlikely). Return plain.
                        return plain;
                    }
                }

                // If file content looks like base64 (no '=' lines), try dpapi decode
                // Heuristic: if file length > 100 and contains only base64 chars -> attempt DPAPI
                var content = File.ReadAllText(path, Encoding.UTF8).Trim();
                if (content.Length > 32 && content.All(c => char.IsLetterOrDigit(c) || c == '+' || c == '/' || c == '=' || char.IsWhiteSpace(c)))
                {
                    var dp = ReadFtpSettingsFromDpapiFile(path);
                    if (dp != null) return dp;
                }
            }
            catch { /* ignore */ }

            // fallback: treat as plain text key=value
            return ReadFtpSettingsFromPlainFile(path);
        }


        private void LoadCredentialsAtStartup()
        {
            try
            {
                // các đường dẫn khả dĩ để tìm file trong khi dev / run / publish
                var candidateFiles = new List<string>();

                // 1) Folder exe hiện tại (thường là bin\Debug\netX\)
                var baseDir = AppDomain.CurrentDomain.BaseDirectory; // đáng tin cậy nhất
                candidateFiles.Add(Path.Combine(baseDir, "ftp_credentials.txt"));
                candidateFiles.Add(Path.Combine(baseDir, "ftp_credentials.dat")); // nếu dùng dpapi

                // 2) Application.StartupPath (WinForms)
                try { candidateFiles.Add(Path.Combine(Application.StartupPath, "ftp_credentials.txt")); } catch { }

                // 3) Assembly location (cũng là exe folder)
                try
                {
                    var asmLoc = Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location ?? Assembly.GetExecutingAssembly().Location);
                    if (!string.IsNullOrEmpty(asmLoc))
                    {
                        candidateFiles.Add(Path.Combine(asmLoc, "ftp_credentials.txt"));
                    }
                }
                catch { }

                // 4) Project root (nếu bạn chạy in VS and working dir is project root)
                try { candidateFiles.Add(Path.Combine(Directory.GetCurrentDirectory(), "ftp_credentials.txt")); } catch { }

                // 5) %APPDATA%\MyApp (our default location) — check too
                var appdata = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MyApp");
                candidateFiles.Add(Path.Combine(appdata, "ftp_credentials.dat"));
                candidateFiles.Add(Path.Combine(appdata, "ftp_credentials.txt"));

                // Remove duplicates and log
                candidateFiles = candidateFiles.Distinct().ToList();
                Log("Bắt đầu tìm file credentials ở các vị trí khả dĩ:");
                foreach (var p in candidateFiles) Log(" -> " + p);

                // Try load
                FtpSettings loaded = null;
                string foundPath = null;
                foreach (var p in candidateFiles)
                {
                    if (File.Exists(p))
                    {
                        Log("Tìm thấy file credentials tại: " + p);
                        loaded = LoadFtpSettingsFromFile(p); // hàm bạn đã có từ trước (detect dpapi/plain)
                        if (loaded != null)
                        {
                            foundPath = p;
                            break;
                        }
                        else
                        {
                            Log("Không đọc được nội dung credentials tại: " + p);
                        }
                    }
                    else
                    {
                        Log("(không có) " + p);
                    }
                }

                if (loaded != null)
                {
                    _ftpSettings = loaded;
                    Log($"Đã load credentials: host={_ftpSettings.Host}, port={_ftpSettings.Port}, user={_ftpSettings.User} (từ {foundPath})");

                    //// Ẩn field UI nếu bạn muốn
                    //SafeInvoke(() =>
                    //{
                    //    if (txtHost != null) txtHost.Visible = false;
                    //    if (txtPort != null) txtPort.Visible = false;
                    //    if (txtUser != null) txtUser.Visible = false;
                    //    if (txtPass != null) txtPass.Visible = false;
                    //    // Show small label to indicate source (optional)
                    //    if (toolStripStatusLabelStatus != null) toolStripStatusLabelStatus.Text = "Credentials: file";
                    //});
                }
                else
                {
                    Log("Không tìm thấy file credentials hợp lệ ở các vị trí trên. Sẽ dùng UI (nếu có).");
                }
            }
            catch (Exception ex)
            {
                Log("Lỗi LoadCredentialsAtStartup: " + ex.Message);
            }
        }

        // --- Utils: hex ---
        private static string ToHex(ReadOnlySpan<byte> bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) sb.AppendFormat("{0:x2}", b);
            return sb.ToString();
        }

        // --- Compute SHA256 of a single file (streamed) ---
        private async Task<string> ComputeFileSha256Async(string filePath, IProgress<long>? progress = null, CancellationToken ct = default)
        {
            const int bufferSize = 1024 * 1024; // 1 MB
            using var sha = SHA256.Create();
            await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, useAsync: true);
            var buffer = new byte[bufferSize];
            long totalRead = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int read = await fs.ReadAsync(buffer, 0, buffer.Length, ct);
                if (read == 0) break;
                if (read == buffer.Length) sha.TransformBlock(buffer, 0, read, null, 0);
                else sha.TransformBlock(buffer, 0, read, null, 0);
                totalRead += read;
                progress?.Report(totalRead);
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return ToHex(sha.Hash);
        }

        // --- Enumerate files deterministically (sorted by relative path) ---
        private IEnumerable<string> EnumerateFilesDeterministic(string rootDir, bool includeSubdirs)
        {
            var root = Path.GetFullPath(rootDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var files = Directory.EnumerateFiles(root, "*", includeSubdirs ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                                 .Select(f =>
                                 {
                                     var rel = Path.GetRelativePath(root, f).Replace(Path.DirectorySeparatorChar, '/');
                                     return (rel, full: f);
                                 })
                                 .OrderBy(x => x.rel, StringComparer.OrdinalIgnoreCase)
                                 .Select(x => x.full);
            return files;
        }

        // --- Generate manifest for a directory (writes to manifestPath) ---
        // manifest format: "<hexsha256>\t<relative/path>\n"
        private async Task GenerateDirectoryManifestAsync(string directoryPath, string manifestPath, bool includeSubdirs = true,
                                                           IProgress<(string relativePath, long fileBytes)>? perFileProgress = null,
                                                           CancellationToken ct = default)
        {
            directoryPath = Path.GetFullPath(directoryPath);
            Directory.CreateDirectory(Path.GetDirectoryName(manifestPath) ?? Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location) ?? ".");
            // ensure manifest written atomically
            var tmp = manifestPath + ".tmp";
            if (File.Exists(tmp)) File.Delete(tmp);

            var files = EnumerateFilesDeterministic(directoryPath, includeSubdirs).ToList();

            await using (var sw = new StreamWriter(tmp, false, Encoding.UTF8))
            {
                foreach (var file in files)
                {
                    ct.ThrowIfCancellationRequested();
                    var rel = Path.GetRelativePath(directoryPath, file).Replace(Path.DirectorySeparatorChar, '/');
                    // compute file sha256 with progress null (or could pass progress)
                    var fileProgress = new Progress<long>(b => perFileProgress?.Report((rel, b)));
                    var hex = await ComputeFileSha256Async(file, fileProgress, ct);
                    await sw.WriteLineAsync($"{hex}\t{rel}");
                }
            }

            // replace manifest atomically
            if (File.Exists(manifestPath)) File.Delete(manifestPath);
            File.Move(tmp, manifestPath);
        }

        // --- Compute a directory "overall" hash from manifest content ---
        // This produces a single SHA256 hex that depends deterministically on manifest lines and order.
        private string ComputeDirectoryHashFromManifestFile(string manifestPath)
        {
            // read all lines in defined order (manifest should already be deterministic)
            using var sha = SHA256.Create();
            using var fs = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            // compute hash on raw bytes to avoid small differences (normalize newlines)
            var buffer = new byte[81920];
            int n;
            while ((n = fs.Read(buffer, 0, buffer.Length)) > 0)
            {
                sha.TransformBlock(buffer, 0, n, null, 0);
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return ToHex(sha.Hash);
        }

        // --- Parse manifest into dictionary: relativePath -> sha ---
        private Dictionary<string, string> ParseManifest(string manifestPath)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in File.ReadAllLines(manifestPath, Encoding.UTF8))
            {
                var line = raw.Trim();
                if (string.IsNullOrEmpty(line)) continue;
                var parts = line.Split('\t', 2);
                if (parts.Length < 2) continue;
                var sha = parts[0].Trim();
                var rel = parts[1].Trim().Replace('\\', '/');
                dict[rel] = sha;
            }
            return dict;
        }

        // --- Verify directory against manifest ---
        // returns lists of missing files, mismatched files (sha differs), extra files (present locally but not in manifest)
        private async Task<(List<string> missing, List<string> mismatched, List<string> extra)> VerifyDirectoryAgainstManifestAsync(
            string directoryPath, string manifestPath, IProgress<(string relativePath, int state)>? progress = null,
            CancellationToken ct = default)
        {
            // state: 0 = checking, 1 = mismatch, 2 = ok, 3 = missing, 4 = extra
            directoryPath = Path.GetFullPath(directoryPath);
            var expected = ParseManifest(manifestPath);
            var missing = new List<string>();
            var mismatched = new List<string>();
            var extra = new List<string>();

            // build set of local files relative
            var localFiles = EnumerateFilesDeterministic(directoryPath, includeSubdirs: true)
                             .Select(f => Path.GetRelativePath(directoryPath, f).Replace(Path.DirectorySeparatorChar, '/'))
                             .ToList();

            var expectedSet = new HashSet<string>(expected.Keys, StringComparer.OrdinalIgnoreCase);

            // check expected files
            foreach (var rel in expected.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report((rel, 0));
                var localPath = Path.Combine(directoryPath, rel.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(localPath))
                {
                    missing.Add(rel);
                    progress?.Report((rel, 3));
                    continue;
                }
                // compute sha
                var sha = await ComputeFileSha256Async(localPath, null, ct);
                if (!string.Equals(sha, expected[rel], StringComparison.OrdinalIgnoreCase))
                {
                    mismatched.Add(rel);
                    progress?.Report((rel, 1));
                }
                else
                {
                    progress?.Report((rel, 2));
                }
            }

            // extra: locals not in expected
            foreach (var rel in localFiles)
            {
                if (!expectedSet.Contains(rel))
                {
                    extra.Add(rel);
                    progress?.Report((rel, 4));
                }
            }

            return (missing, mismatched, extra);
        }

        // --- Verify a child directory against parent manifest entries under a given prefix ---
        // parentManifest maps relative paths from parentRoot; childDir is a subpath under parentRoot (or separate dir).
        // Use parentPrefix = path of child relative to parentRoot (use '/' separator).
        private async Task<(List<string> missing, List<string> mismatched, List<string> extra)> VerifyChildAgainstParentManifestAsync(
            string parentManifestPath, string parentRootPath, string childRelativePath, string childDirPath,
            IProgress<(string relativePath, int state)>? progress = null, CancellationToken ct = default)
        {
            parentRootPath = Path.GetFullPath(parentRootPath);
            childDirPath = Path.GetFullPath(childDirPath);
            // Normalize childRelativePath to use '/'
            childRelativePath = childRelativePath.Trim().Trim('/');
            if (childRelativePath == ".") childRelativePath = "";

            var parentDict = ParseManifest(parentManifestPath);
            // Select entries whose relative path starts with childRelativePath + '/'
            string prefix = string.IsNullOrEmpty(childRelativePath) ? "" : (childRelativePath + "/");

            // build expected mapping for this child: key is child-relative path (relative to child root)
            var expectedForChild = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in parentDict)
            {
                if (string.IsNullOrEmpty(prefix))
                {
                    // if childRelativePath empty -> parent manifest same as child root (compare intersection)
                    // include files whose path does not go outside childDir? best-effort: include those that start with nothing (all)
                    // but we should restrict to paths that actually belong to childDir => detect by checking if kv.Key starts with '' -> all
                    // For safety, we only include entries where path, when combined, resolves under childDir
                    // Simpler: include entries whose path contains childRelativePath as prefix
                }
                if (string.IsNullOrEmpty(prefix))
                {
                    // include entries that are under childDirPath when combined with parentRootPath
                    var abs = Path.GetFullPath(Path.Combine(parentRootPath, kv.Key.Replace('/', Path.DirectorySeparatorChar)));
                    if (abs.StartsWith(childDirPath, StringComparison.OrdinalIgnoreCase))
                    {
                        // compute relative to childDirPath
                        var relToChild = Path.GetRelativePath(childDirPath, abs).Replace(Path.DirectorySeparatorChar, '/');
                        expectedForChild[relToChild] = kv.Value;
                    }
                }
                else
                {
                    if (kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        var relToChild = kv.Key.Substring(prefix.Length).TrimStart('/');
                        expectedForChild[relToChild] = kv.Value;
                    }
                }
            }

            // write a temporary manifest for the child then call VerifyDirectoryAgainstManifestAsync-like logic
            // Instead of writing, use the dictionary expectedForChild and compare to childDir contents
            var missing = new List<string>();
            var mismatched = new List<string>();
            var extra = new List<string>();

            // list local files in childDir
            var localFiles = Directory.EnumerateFiles(childDirPath, "*", SearchOption.AllDirectories)
                                .Select(f => Path.GetRelativePath(childDirPath, f).Replace(Path.DirectorySeparatorChar, '/'))
                                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                                .ToList();

            var expectedSet = new HashSet<string>(expectedForChild.Keys, StringComparer.OrdinalIgnoreCase);

            foreach (var expectedRel in expectedForChild.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report((expectedRel, 0));
                var localPath = Path.Combine(childDirPath, expectedRel.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(localPath))
                {
                    missing.Add(expectedRel);
                    progress?.Report((expectedRel, 3));
                    continue;
                }
                var sha = await ComputeFileSha256Async(localPath, null, ct);
                if (!string.Equals(sha, expectedForChild[expectedRel], StringComparison.OrdinalIgnoreCase))
                {
                    mismatched.Add(expectedRel);
                    progress?.Report((expectedRel, 1));
                }
                else progress?.Report((expectedRel, 2));
            }

            // find extras
            foreach (var rel in localFiles)
            {
                if (!expectedSet.Contains(rel))
                {
                    extra.Add(rel);
                    progress?.Report((rel, 4));
                }
            }

            return (missing, mismatched, extra);
        }

        // Helper: gọi GetListing / GetListingAsync qua reflection, trả IEnumerable items (or null)
        private async Task<IEnumerable<object>> GetRemoteListingAsync(string remoteDir)
        {
            try
            {
                var clientType = _client.GetType();
                var miAsync = clientType.GetMethod("GetListingAsync", new Type[] { typeof(string) });
                if (miAsync != null)
                {
                    var task = (Task)miAsync.Invoke(_client, new object[] { remoteDir });
                    await task;
                    var res = task.GetType().GetProperty("Result")?.GetValue(task) as System.Collections.IEnumerable;
                    return res?.Cast<object>() ?? Enumerable.Empty<object>();
                }

                var miSync = clientType.GetMethod("GetListing", new Type[] { typeof(string) });
                if (miSync != null)
                {
                    var res = miSync.Invoke(_client, new object[] { remoteDir }) as System.Collections.IEnumerable;
                    return res?.Cast<object>() ?? Enumerable.Empty<object>();
                }

                Log("GetRemoteListingAsync: client không hỗ trợ GetListing/GetListingAsync.");
                return Enumerable.Empty<object>();
            }
            catch (Exception ex)
            {
                Log("GetRemoteListingAsync lỗi: " + ex.Message);
                return Enumerable.Empty<object>();
            }
        }

        // Helper: kiểm tra remote path là file hay không (sử dụng FileExists nếu có, fallback GetListing)
        private async Task<bool> IsRemoteFileSafeAsync(string remotePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(remotePath)) return false;
                // Try FileExistsAsync / FileExists via reflection first
                var clientType = _client.GetType();
                var miFileExistsAsync = clientType.GetMethod("FileExistsAsync", new Type[] { typeof(string) });
                if (miFileExistsAsync != null)
                {
                    var t = (Task)miFileExistsAsync.Invoke(_client, new object[] { remotePath });
                    await t;
                    var result = t.GetType().GetProperty("Result")?.GetValue(t);
                    if (result is bool b) return b;
                }
                else
                {
                    var miFileExists = clientType.GetMethod("FileExists", new Type[] { typeof(string) });
                    if (miFileExists != null)
                    {
                        var res = miFileExists.Invoke(_client, new object[] { remotePath });
                        if (res is bool b2) return b2;
                    }
                }

                // Fallback: use GetListing on parent folder and match item name/type
                var parent = remotePath.Replace('\\', '/');
                if (parent.EndsWith("/")) parent = parent.TrimEnd('/');
                var idx = parent.LastIndexOf('/');
                string dir = "/";
                string name = parent;
                if (idx >= 0)
                {
                    dir = parent.Substring(0, idx + 1); // keep trailing '/'
                    name = parent.Substring(idx + 1);
                }

                var listing = await GetRemoteListingAsync(dir);
                foreach (var item in listing)
                {
                    try
                    {
                        var nameProp = item.GetType().GetProperty("Name");
                        var typeProp = item.GetType().GetProperty("Type"); // might be FtpObjectType or FtpListItemType
                        var sizeProp = item.GetType().GetProperty("Size");

                        var nm = nameProp?.GetValue(item)?.ToString();
                        if (!string.Equals(nm, name, StringComparison.OrdinalIgnoreCase)) continue;

                        // If the listing provides a Type, use it
                        var tval = typeProp?.GetValue(item);
                        if (tval != null)
                        {
                            var tstr = tval.ToString().ToLowerInvariant();
                            if (tstr.Contains("file") || tstr.Contains("file-type") || tstr.Contains("fileentry")) return true;
                            if (tstr.Contains("dir") || tstr.Contains("directory") || tstr.Contains("folder")) return false;
                        }

                        // If Size property indicates presence -> file
                        var sval = sizeProp?.GetValue(item);
                        if (sval != null)
                        {
                            // has size -> treat as file (even 0-length is likely a file)
                            return true;
                        }

                        // IMPORTANT SAFE BEHAVIOR:
                        // Many servers (FileZilla MLSx) return entries with no Type/Size for directories.
                        // If both Type and Size are missing we should conservatively treat the entry as a directory
                        // to avoid issuing RETR on a directory (which produces 550 Permission denied).
                        Log($"IsRemoteFileSafeAsync: listing item '{nm}' has no Type/Size -> treat as directory (conservative). Candidate: {remotePath}");
                        return false;

                        // Note: we purposely avoid calling DecideIsDirectoryAsync here because
                        // that can trigger additional network calls (SIZE/RETR) that may fail with 550.
                    }
                    catch { continue; }
                }

                return false;
            }
            catch (Exception ex)
            {
                Log("IsRemoteFileSafeAsync lỗi: " + ex.Message);
                return false;
            }
        }


        // Helper: download remote file safely (check type first), trả true nếu thành công
        // Improve: after a failed/zero-byte DownloadFileAsync, resolve real path and retry; if still bad, use CWD+relative stream fallback
        private async Task<bool> DownloadRemoteFileSafeAsync(string remotePath, string localPath, CancellationToken ct = default, IProgress<FluentFTP.FtpProgress> perFileProgress = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(remotePath)) return false;
                var norm = NormalizeFtpPath(remotePath);

                // Best-effort size probe
                var size = await TryGetRemoteFileSizeAsync(norm);
                if (size.HasValue)
                {
                    Log($"Remote size for '{norm}' = {size.Value} bytes");
                    if (size.Value == 0) Log($"Remote file '{norm}' size==0 — sẽ thử download nhưng cảnh báo.");
                }
                else
                {
                    Log($"Không lấy được size cho '{norm}' (server/command không hỗ trợ).");
                }

                // Ensure likely a file
                var isFile = await IsRemoteFileSafeAsync(norm);
                if (!isFile)
                {
                    Log("DownloadRemoteFileSafeAsync: remote không phải file hoặc không tồn tại: " + norm);
                    return false;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(localPath) ?? ".");

                var clientType = _client.GetType();
                var methods = clientType.GetMethods().Where(m => m.Name.StartsWith("DownloadFile", StringComparison.OrdinalIgnoreCase)).ToList();

                bool TryCheckZeroAndCleanup()
                {
                    try
                    {
                        var fi = new FileInfo(localPath);
                        if (!fi.Exists || fi.Length == 0)
                        {
                            Log($"DownloadRemoteFileSafeAsync completed but local file missing/zero: {localPath}");
                            try { if (fi.Exists) fi.Delete(); } catch { }
                            return true; // failure
                        }
                    }
                    catch (Exception ex)
                    {
                        Log("Lỗi kiểm tra file local sau download: " + ex.Message);
                        return true; // failure
                    }
                    return false; // ok
                }

                async Task<bool> TryResolvedThenStreamFallbackAsync()
                {
                    // Try resolved path (via listing)
                    var resolved = await ResolveRemoteFilePathAsync(norm);
                    if (!string.IsNullOrWhiteSpace(resolved) && !string.Equals(resolved, norm, StringComparison.OrdinalIgnoreCase))
                    {
                        Log($"DownloadRemoteFileSafeAsync: thử lại với đường dẫn đã resolve: {resolved}");
                        if (methods.Any(m => m.Name.EndsWith("Async", StringComparison.OrdinalIgnoreCase)))
                        {
                            var miAsync2 = methods.First(m =>
                            {
                                var p = m.GetParameters();
                                return m.Name.EndsWith("Async", StringComparison.OrdinalIgnoreCase)
                                       && p.Length >= 2 && p[0].ParameterType == typeof(string) && p[1].ParameterType == typeof(string);
                            });
                            var args2 = new List<object> { localPath, resolved };
                            foreach (var p in miAsync2.GetParameters().Skip(2))
                            {
                                var pt = p.ParameterType;
                                if (pt == typeof(FluentFTP.FtpLocalExists)) args2.Add(FluentFTP.FtpLocalExists.Overwrite);
                                else if (pt == typeof(FluentFTP.FtpVerify)) args2.Add(FluentFTP.FtpVerify.None);
                                else if (pt == typeof(IProgress<FluentFTP.FtpProgress>)) args2.Add(perFileProgress);
                                else if (pt == typeof(CancellationToken)) args2.Add(ct);
                                else args2.Add(Type.Missing);
                            }
                            var task2 = (Task)miAsync2.Invoke(_client, args2.ToArray());
                            await task2;
                            if (!TryCheckZeroAndCleanup())
                            {
                                Log($"DownloadRemoteFileSafeAsync thành công (resolved): {localPath} ({new FileInfo(localPath).Length} bytes)");
                                return true;
                            }
                        }
                    }

                    // Stream fallback (abs -> resolved -> CWD+relative handled inside)
                    Log("DownloadRemoteFileSafeAsync: thử fallback stream (OpenRead).");
                    return await DownloadByStreamFallbackAsync(norm, localPath, ct);
                }

                // 1) Primary async path
                var miAsync = methods.FirstOrDefault(m =>
                {
                    var p = m.GetParameters();
                    if (!m.Name.EndsWith("Async", StringComparison.OrdinalIgnoreCase)) return false;
                    return p.Length >= 2 && p[0].ParameterType == typeof(string) && p[1].ParameterType == typeof(string);
                });

                if (miAsync != null)
                {
                    var args = new List<object> { localPath, norm };
                    foreach (var p in miAsync.GetParameters().Skip(2))
                    {
                        var pt = p.ParameterType;
                        if (pt == typeof(FluentFTP.FtpLocalExists)) args.Add(FluentFTP.FtpLocalExists.Overwrite);
                        else if (pt == typeof(FluentFTP.FtpVerify)) args.Add(FluentFTP.FtpVerify.None);
                        else if (pt == typeof(IProgress<FluentFTP.FtpProgress>)) args.Add(perFileProgress);
                        else if (pt == typeof(CancellationToken)) args.Add(ct);
                        else args.Add(Type.Missing);
                    }

                    var task = (Task)miAsync.Invoke(_client, args.ToArray());
                    await task;

                    // Check FtpStatus if returned
                    try
                    {
                        var resultProp = task.GetType().GetProperty("Result");
                        if (resultProp != null)
                        {
                            var statusVal = resultProp.GetValue(task);
                            var statusStr = statusVal?.ToString() ?? "Unknown";
                            if (!string.Equals(statusStr, "Success", StringComparison.OrdinalIgnoreCase))
                            {
                                Log($"DownloadFileAsync FtpStatus={statusStr} cho '{norm}' -> thử resolve và fallback stream.");
                                try { if (File.Exists(localPath)) File.Delete(localPath); } catch { }
                                return await TryResolvedThenStreamFallbackAsync();
                            }
                        }
                    }
                    catch { /* ignore */ }

                    if (TryCheckZeroAndCleanup())
                    {
                        // Retry with resolve then stream
                        return await TryResolvedThenStreamFallbackAsync();
                    }

                    Log($"DownloadRemoteFileSafeAsync thành công: {localPath} ({new FileInfo(localPath).Length} bytes)");
                    return true;
                }

                // 2) Fallback sync DownloadFile
                var miSync = methods.FirstOrDefault(m => !m.Name.EndsWith("Async", StringComparison.OrdinalIgnoreCase)
                                                        && m.GetParameters().Length >= 2
                                                        && m.GetParameters()[0].ParameterType == typeof(string)
                                                        && m.GetParameters()[1].ParameterType == typeof(string));
                if (miSync != null)
                {
                    miSync.Invoke(_client, new object[] { localPath, norm });
                    if (TryCheckZeroAndCleanup())
                    {
                        return await TryResolvedThenStreamFallbackAsync();
                    }
                    Log($"DownloadRemoteFileSafeAsync(sync) thành công: {localPath} ({new FileInfo(localPath).Length} bytes)");
                    return true;
                }

                // 3) Stream fallback if no suitable DownloadFile found
                Log("DownloadRemoteFileSafeAsync: Không tìm thấy phương thức download phù hợp trên client. Thử fallback stream.");
                return await DownloadByStreamFallbackAsync(norm, localPath, ct);
            }
            catch (TargetInvocationException tie)
            {
                var inner = tie.InnerException ?? tie;
                Log("DownloadRemoteFileSafeAsync: lỗi khi gọi FluentFTP: " + inner.Message);
                Log("DownloadRemoteFileSafeAsync: FluentFTP InnerException detail: " + inner.ToString());
                try { if (File.Exists(localPath)) File.Delete(localPath); } catch { }
                return false;
            }
            catch (OperationCanceledException)
            {
                Log("DownloadRemoteFileSafeAsync: bị hủy bởi người dùng.");
                try { if (File.Exists(localPath)) File.Delete(localPath); } catch { }
                throw;
            }
            catch (Exception ex)
            {
                Log("DownloadRemoteFileSafeAsync lỗi: " + ex.Message);
                Log("DownloadRemoteFileSafeAsync exception detail: " + ex.ToString());
                try { if (File.Exists(localPath)) File.Delete(localPath); } catch { }
                return false;
            }
        }
        // dùng helper to find + download manifest safely
        private async Task<string> FindRemoteManifestInDirectorySafeAsync(string remoteDir)
        {
            var listing = await GetRemoteListingAsync(remoteDir);
            var candidateNames = new[] { "checksums.txt", "manifest.txt", "hashes.txt", "checksums.sha256", "sha256sums.txt", "checksums.md5" };
            var lowerCandidates = new HashSet<string>(candidateNames.Select(x => x.ToLowerInvariant()));

            // Log listing details (helpful to debug why server "hides" files)
            try
            {
                Log($"Listing for '{remoteDir}':");
                foreach (var item in listing)
                {
                    try
                    {
                        var name = item.GetType().GetProperty("Name")?.GetValue(item)?.ToString();
                        var full = item.GetType().GetProperty("FullName")?.GetValue(item)?.ToString();
                        var sizeProp = item.GetType().GetProperty("Size");
                        var sizeVal = sizeProp?.GetValue(item);
                        string sizeStr = sizeVal != null ? sizeVal.ToString() : "?";
                        Log($" - {name}  full='{full}'  size={sizeStr}");
                    }
                    catch { }
                }
            }
            catch { }

            foreach (var item in listing)
            {
                try
                {
                    var name = item.GetType().GetProperty("Name")?.GetValue(item)?.ToString();
                    var full = item.GetType().GetProperty("FullName")?.GetValue(item)?.ToString();
                    if (string.IsNullOrEmpty(name)) continue;
                    if (!lowerCandidates.Contains(name.ToLowerInvariant())) continue;

                    var remotePath = full ?? (remoteDir.TrimEnd('/') + "/" + name);
                    remotePath = NormalizeFtpPath(remotePath);
                    Log("Found candidate manifest name -> trying: " + remotePath);

                    // pre-check remote size
                    var rsize = await TryGetRemoteFileSizeAsync(remotePath);
                    if (rsize.HasValue)
                    {
                        Log($"Candidate '{remotePath}' reported size {rsize.Value} bytes");
                        if (rsize.Value == 0)
                        {
                            Log($"Candidate manifest '{remotePath}' size==0, bỏ qua.");
                            continue;
                        }
                    }
                    else
                    {
                        Log($"Không lấy được size cho candidate '{remotePath}', sẽ thử download để kiểm tra nội dung.");
                    }

                    var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + "_" + name);
                    Log($"Trying to read remote version file: {remotePath}");
                    var ok = await DownloadRemoteFileSafeAsync(remotePath, tmp);
                    if (!ok)
                    {
                        Log($"DownloadRemoteFileSafeAsync failed for {remotePath}. Trying direct download fallback.");
                        var ok2 = await TryDownloadFileDirectAsync(remotePath, tmp);
                        if (!ok2)
                        {
                            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                            continue;
                        }
                    }

                    // quick validate: check first few lines match manifest pattern (sha TAB path)
                    try
                    {
                        var lines = File.ReadAllLines(tmp, Encoding.UTF8);
                        bool looksLikeManifest = false;
                        var regex = new System.Text.RegularExpressions.Regex(@"^[0-9a-fA-F]{64}\t.+"); // sha256<TAB>path
                        foreach (var l in lines.Take(10))
                        {
                            if (string.IsNullOrWhiteSpace(l)) continue;
                            if (regex.IsMatch(l.Trim()))
                            {
                                looksLikeManifest = true;
                                break;
                            }
                        }

                        if (looksLikeManifest)
                        {
                            Log("Manifest candidate validated: " + remotePath);
                            return tmp;
                        }
                        else
                        {
                            Log("Downloaded file did not look like a manifest -> deleting temp and continuing: " + tmp);
                            try { File.Delete(tmp); } catch { }
                            continue;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log("Error validating downloaded candidate manifest: " + ex.Message);
                        try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    Log("FindRemoteManifestInDirectorySafeAsync item error: " + ex.Message);
                    continue;
                }
            }
            return null;
        }

        private async Task<long?> TryGetRemoteFileSizeAsync(string remotePath)
        {
            try
            {
                var clientType = _client.GetType();
                var miAsync = clientType.GetMethod("GetFileSizeAsync", new Type[] { typeof(string) });
                if (miAsync != null)
                {
                    var t = (Task)miAsync.Invoke(_client, new object[] { remotePath });
                    await t;
                    var rp = t.GetType().GetProperty("Result");
                    if (rp != null)
                    {
                        var res = rp.GetValue(t);
                        if (res != null) return Convert.ToInt64(res);
                    }
                }

                var miSync = clientType.GetMethod("GetFileSize", new Type[] { typeof(string) });
                if (miSync != null)
                {
                    var res = miSync.Invoke(_client, new object[] { remotePath });
                    if (res != null) return Convert.ToInt64(res);
                }
            }
            catch (Exception ex)
            {
                Log("TryGetRemoteFileSizeAsync lỗi: " + ex.Message);
            }
            return null;
        }

        // --- BEGIN: Version sync helpers (inserted) ---
        private async Task<List<string>> GetRemoteVersionFoldersAsync(string remoteRoot)
        {
            var res = new List<string>();
            try
            {
                var items = await GetRemoteListingAsync(remoteRoot);
                foreach (var it in items)
                {
                    try
                    {
                        var info = InspectListingItem(it);
                        if (string.IsNullOrEmpty(info.Name)) continue;

                        // Build candidate full path for the item
                        var candidate = !string.IsNullOrEmpty(info.FullName)
                            ? NormalizeFtpPath(info.FullName)
                            : NormalizeFtpPath(remoteRoot.TrimEnd('/') + "/" + info.Name);

                        bool isDir = false;

                        // 1) If listing provided a Type, trust it when it explicitly indicates file or directory
                        if (!string.IsNullOrEmpty(info.Type))
                        {
                            var t = info.Type.ToLowerInvariant();
                            if (t.Contains("dir") || t.Contains("directory") || t.Contains("folder")) isDir = true;
                            else if (t.Contains("file")) isDir = false;
                        }

                        // 2) If Type was not decisive, use safer network-backed heuristic:
                        //    call DecideIsDirectoryAsync which falls back to DirectoryExists/GetListing checks.
                        //    This avoids mis-classifying files as folders when Size property is missing.
                        if (string.IsNullOrEmpty(info.Type) || (!isDir && !info.Type.ToLowerInvariant().Contains("file")))
                        {
                            try
                            {
                                // DecideIsDirectoryAsync may perform network calls; it's the authoritative check.
                                isDir = await DecideIsDirectoryAsync(it, candidate);
                            }
                            catch (Exception ex)
                            {
                                Log($"DecideIsDirectoryAsync failed for '{candidate}': {ex.Message}");
                                // keep previous isDir (likely false)
                            }
                        }

                        if (isDir)
                        {
                            res.Add(candidate);
                        }
                    }
                    catch (Exception exItem)
                    {
                        Log("GetRemoteVersionFoldersAsync: skip item error: " + exItem.Message);
                        continue;
                    }
                }
            }
            catch (Exception ex)
            {
                Log("GetRemoteVersionFoldersAsync lỗi: " + ex.Message);
            }
            return res;
        }

        private async Task<bool> SyncLocalToVersionAsync(string versionRemoteFolder, string localAppFolder, CancellationToken ct, IProgress<string>? status = null)
        {
            status?.Report($"Prepare sync: {versionRemoteFolder}");
            string localManifestTemp = null;
            try
            {
                localManifestTemp = await FindRemoteManifestInDirectorySafeAsync(versionRemoteFolder);
                if (string.IsNullOrEmpty(localManifestTemp) || !File.Exists(localManifestTemp))
                {
                    Log($"Không tìm thấy manifest trong {versionRemoteFolder}");
                    return false;
                }

                var expected = ParseManifest(localManifestTemp);
                if (expected == null || expected.Count == 0)
                {
                    Log("Manifest rỗng hoặc không hợp lệ: " + localManifestTemp);
                    return false;
                }

                // Skip syncing the manifest/checksum files themselves to avoid self-hash issues
                var expectedFiles = expected
                    .Where(kv => !IsManifestRelPath(kv.Key))
                    .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

                Directory.CreateDirectory(localAppFolder);

                // 1) Plan: figure which files need download and their sizes
                var plan = new List<(string rel, string remote, string local, string expectedSha, long? size)>();
                foreach (var kv in expectedFiles.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                {
                    ct.ThrowIfCancellationRequested();

                    var rel = kv.Key.Replace('\\', '/').TrimStart('/');
                    var expectedSha = kv.Value;
                    var localPath = Path.Combine(localAppFolder, rel.Replace('/', Path.DirectorySeparatorChar));
                    var localDir = Path.GetDirectoryName(localPath) ?? localAppFolder;
                    Directory.CreateDirectory(localDir);

                    bool needDownload = false;
                    if (!File.Exists(localPath))
                    {
                        needDownload = true;
                        status?.Report("MISSING: " + rel);
                    }
                    else
                    {
                        try
                        {
                            var sha = await ComputeFileSha256Async(localPath, null, ct);
                            if (!string.Equals(sha, expectedSha, StringComparison.OrdinalIgnoreCase))
                            {
                                needDownload = true;
                                status?.Report("MISMATCH: " + rel);
                            }
                        }
                        catch (Exception ex)
                        {
                            Log($"Lỗi tính sha '{localPath}': {ex.Message}");
                            needDownload = true;
                        }
                    }

                    if (!needDownload) continue;

                    var remoteFile = NormalizeFtpPath(versionRemoteFolder.TrimEnd('/') + "/" + rel);
                    long? size = await TryGetRemoteFileSizeAsync(remoteFile);
                    plan.Add((rel, remoteFile, localPath, expectedSha, size));
                }

                var knownTotal = plan.Where(p => p.size.HasValue).Sum(p => p.size!.Value);
                Log($"Sync plan: {plan.Count} file(s) to download. Known total bytes = {knownTotal}");
                var overall = new OverallTransfer(this, knownTotal, plan.Count);

                // 2) Execute: download with overall progress
                int done = 0;
                foreach (var p in plan)
                {
                    ct.ThrowIfCancellationRequested();

                    var tmpLocal = p.local + ".tmp";
                    try { if (File.Exists(tmpLocal)) File.Delete(tmpLocal); } catch { }

                    status?.Report($"Downloading ({++done}/{plan.Count}): {p.rel}");
                    Log($"Sync: {p.remote} -> {tmpLocal}");

                    var perFile = overall.CreatePerFileProgress(p.remote, p.size);
                    var ok = await DownloadRemoteFileSafeAsync(p.remote, tmpLocal, ct, perFile);
                    if (!ok)
                    {
                        Log($"Tải file thất bại: {p.remote}");
                        try { if (File.Exists(tmpLocal)) File.Delete(tmpLocal); } catch { }
                        return false;
                    }

                    try
                    {
                        var downloadedSha = await ComputeFileSha256Async(tmpLocal, null, ct);
                        if (!string.Equals(downloadedSha, p.expectedSha, StringComparison.OrdinalIgnoreCase))
                        {
                            Log($"SHA mismatch for {p.rel}: expected {p.expectedSha} got {downloadedSha}");
                            try { if (File.Exists(tmpLocal)) File.Delete(tmpLocal); } catch { }
                            return false;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log("Lỗi verify downloaded sha: " + ex.Message);
                        try { if (File.Exists(tmpLocal)) File.Delete(tmpLocal); } catch { }
                        return false;
                    }

                    try
                    {
                        if (File.Exists(p.local)) File.Delete(p.local);
                        File.Move(tmpLocal, p.local);
                        status?.Report("Updated: " + p.rel);
                        Log("Updated: " + p.local);
                    }
                    catch (Exception ex)
                    {
                        Log($"Không thể cập nhật file '{p.local}': {ex.Message}");
                        try { if (File.Exists(tmpLocal)) File.Delete(tmpLocal); } catch { }
                        return false;
                    }
                }

                return true;
            }
            finally
            {
                try { if (!string.IsNullOrEmpty(localManifestTemp) && File.Exists(localManifestTemp)) File.Delete(localManifestTemp); } catch { }
            }
        }

        private async Task CheckAndPerformSequentialVersionUpdatesAsync(string remoteRoot)
        {
            if (_client == null || !_client.IsConnected)
            {
                MessageBox.Show("Chưa kết nối FTP. Vui lòng kết nối trước khi cập nhật.");
                return;
            }

            var localAppFolder = txtLocal?.Text?.Trim();
            if (string.IsNullOrEmpty(localAppFolder))
            {
                MessageBox.Show("Chưa chọn thư mục local của ứng dụng. Vui lòng chọn Local folder.");
                return;
            }
            Directory.CreateDirectory(localAppFolder);

            // NEW: Require a valid local version first
            var localVerDetected = GetLocalVersionFromAppFolder(localAppFolder);
            if (string.IsNullOrWhiteSpace(localVerDetected) || string.Equals(localVerDetected, "0.0.0", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("Không xác định được phiên bản trong thư mục local. Dừng cập nhật.", "Cập nhật", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                SetStatus("Không xác định được phiên bản local.", true);
                return;
            }

            // NEW: Require root folder to have a version.json
            var rootToken = await ReadRemoteVersionFromFolderAsync(remoteRoot);
            if (string.IsNullOrWhiteSpace(rootToken))
            {
                MessageBox.Show($"Không tìm thấy file version.json hợp lệ trong thư mục gốc '{remoteRoot}'. Dừng cập nhật.", "Cập nhật", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                Log($"Root '{remoteRoot}' không có version.json hợp lệ.");
                SetStatus("Không có version tại thư mục gốc.", true);
                return;
            }

            SetStatus("Lấy danh sách phiên bản từ server...");

            // First try global manifest at remoteRoot/version.json
            List<(string folder, string token)> versionMap;
            var global = await ReadGlobalVersionManifestAsync(remoteRoot);
            if (global != null && global.Count > 0)
            {
                versionMap = global;
                Log("Using global version.json to enumerate versions.");
            }
            else
            {
                var folders = await GetRemoteVersionFoldersAsync(remoteRoot);
                if (folders == null || folders.Count == 0)
                {
                    MessageBox.Show("Không tìm thấy thư mục phiên bản trên server.");
                    SetStatus("Không có phiên bản trên server.", true);
                    return;
                }

                SetStatus("Đọc token phiên bản từ các thư mục trên server (yêu cầu version.json)...");
                var sem = new SemaphoreSlim(4);
                var tasks = new List<Task>();
                var map = new List<(string folder, string token)>();

                var scanProgress = new Progress<string>(s =>
                {
                    try { toolStripStatusLabelStatus.Text = s; } catch { }
                    Log(s);
                });

                foreach (var f in folders)
                {
                    var folder = f;
                    var t = Task.Run(async () =>
                    {
                        await sem.WaitAsync();
                        try
                        {
                            ((IProgress<string>)scanProgress).Report($"Đọc phiên bản: {folder}");
                            string token = null;
                            try
                            {
                                token = await ReadRemoteVersionFromFolderAsync(folder);
                            }
                            catch (Exception ex)
                            {
                                Log($"Lỗi khi đọc phiên bản từ '{folder}': {ex.Message}");
                                token = null;
                            }

                            lock (map)
                            {
                                map.Add((folder, token));
                            }
                        }
                        finally
                        {
                            sem.Release();
                        }
                    });

                    tasks.Add(t);
                }

                await Task.WhenAll(tasks);
                try { sem.Dispose(); } catch { }

                var missingVersionFiles = map.Where(v => string.IsNullOrWhiteSpace(v.token)).Select(v => v.folder).ToList();
                if (missingVersionFiles.Any())
                {
                    var sbErr = new StringBuilder();
                    sbErr.AppendLine("Không tìm thấy hoặc không valid file 'version.json' trong các thư mục sau:");
                    foreach (var mf in missingVersionFiles) sbErr.AppendLine(" - " + mf);
                    sbErr.AppendLine();
                    sbErr.AppendLine("Cập nhật bị hủy. Vui lòng đảm bảo mỗi thư mục phiên bản chứa file 'version.json' hợp lệ.");

                    MessageBox.Show(sbErr.ToString(), "Lỗi: Thiếu version.json", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    SetStatus("Thiếu version.json trong một hoặc nhiều thư mục phiên bản.", true);
                    return;
                }

                versionMap = map;
            }

            // sort by semver (ascending)
            versionMap = versionMap
                .OrderBy(v => v.token, Comparer<string>.Create((a, b) => CompareSemverVersions(a, b)))
                .ToList();

            // read local version from target app folder
            var localVer = GetLocalVersionFromAppFolder(localAppFolder);

            // pick pending versions greater than local
            var pending = versionMap.Where(v => CompareSemverVersions(localVer, v.token) < 0).ToList();
            if (!pending.Any())
            {
                MessageBox.Show($"Local version ({localVer}) là mới nhất hoặc không tìm thấy phiên bản lớn hơn trên server.");
                SetStatus("Đã là bản mới nhất.");
                return;
            }

            // AUTO: no confirmation, proceed directly
            Log($"Auto applying {pending.Count} version(s) sequentially (low -> high): "
                + string.Join(", ", pending.Select(p => p.token)));

            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            SetUiBusy(true);
            var prevCursor = Cursor;
            Cursor = Cursors.WaitCursor;
            try
            {
                foreach (var p in pending)
                {
                    ct.ThrowIfCancellationRequested();
                    SetStatus($"Áp dụng phiên bản {p.token} ...");
                    Log($"Apply version {p.token} from {p.folder}");

                    var progApply = new Progress<string>(s =>
                    {
                        try { toolStripStatusLabelStatus.Text = s; } catch { }
                        Log(s);
                    });

                    // Directly sync to target version. No cross-version manifest comparison.
                    var ok = await SyncLocalToVersionAsync(p.folder, localAppFolder, ct, progApply);
                    if (!ok)
                    {
                        MessageBox.Show($"Áp dụng phiên bản {p.token} thất bại. Hủy chuỗi cập nhật.", "Lỗi", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        SetStatus($"Thất bại khi áp dụng {p.token}", true);
                        return;
                    }

                    localVer = p.token;
                    Log($"Applied version {p.token}");
                }

                MessageBox.Show("Cập nhật tuần tự hoàn tất.", "Hoàn tất", MessageBoxButtons.OK, MessageBoxIcon.Information);
                SetStatus("Hoàn tất cập nhật phiên bản.");
            }
            catch (OperationCanceledException)
            {
                Log("Cập nhật tuần tự bị hủy.");
                MessageBox.Show("Đã hủy cập nhật.", "Hủy", MessageBoxButtons.OK, MessageBoxIcon.Information);
                SetStatus("Đã hủy cập nhật.", true);
            }
            catch (Exception ex)
            {
                Log("Lỗi khi cập nhật tuần tự: " + ex.Message);
                MessageBox.Show("Lỗi khi cập nhật: " + ex.Message, "Lỗi", MessageBoxButtons.OK, MessageBoxIcon.Error);
                SetStatus("Lỗi cập nhật.", true);
            }
            finally
            {
                SetUiBusy(false);
                Cursor = prevCursor;
                try { progressBar.Value = 0; } catch { }
                _cts?.Dispose();
                _cts = null;
            }
        }
        //  read version from target app folder instead of this manager assembly ---
        private string GetLocalVersionFromAppFolder(string appFolder)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(appFolder)) return "0.0.0";
                appFolder = Path.GetFullPath(appFolder);

                // 1) Prefer explicit version file: version.txt
                var txtPath = Path.Combine(appFolder, "version.txt");
                if (File.Exists(txtPath))
                {
                    var v = File.ReadAllText(txtPath, Encoding.UTF8).Trim();
                    if (!string.IsNullOrEmpty(v)) return v;
                }

                // 2) Try version.json with { "version": "x.y.z" }
                var jsonPath = Path.Combine(appFolder, "version.json");
                if (File.Exists(jsonPath))
                {
                    try
                    {
                        var txt = File.ReadAllText(jsonPath, Encoding.UTF8);
                        using var doc = JsonDocument.Parse(txt);
                        if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("version", out var verProp))
                        {
                            var v = verProp.GetString();
                            if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
                        }
                    }
                    catch { /* ignore malformed json */ }
                }

                // 3) Inspect executables in folder (prefer file/product version)
                var exes = Directory.EnumerateFiles(appFolder, "*.exe", SearchOption.TopDirectoryOnly).ToList();
                if (!exes.Any())
                {
                    // fallback to search subfolders (in case app is in subfolder)
                    exes = Directory.EnumerateFiles(appFolder, "*.exe", SearchOption.AllDirectories).ToList();
                }

                string bestVer = null;
                foreach (var exe in exes)
                {
                    try
                    {
                        var fvi = FileVersionInfo.GetVersionInfo(exe);
                        var ver = fvi.ProductVersion ?? fvi.FileVersion;
                        if (string.IsNullOrWhiteSpace(ver)) continue;
                        ver = ver.Trim();
                        if (bestVer == null)
                        {
                            bestVer = ver;
                        }
                        else
                        {
                            // choose the greater semantic version using existing CompareSemverVersions
                            try
                            {
                                if (CompareSemverVersions(bestVer, ver) < 0) bestVer = ver;
                            }
                            catch { /* ignore compare errors */ }
                        }
                    }
                    catch { /* ignore per-file errors */ }
                }

                if (!string.IsNullOrWhiteSpace(bestVer)) return bestVer;

                // 4) fallback: return "0.0.0"
                return "0.0.0";
            }
            catch
            {
                return "0.0.0";
            }
        }

        // Read remote version token from a remote folder (tries version.json, version.txt, then folder name)
        private async Task<string> ReadRemoteVersionFromFolderAsync(string remoteFolder)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(remoteFolder)) return null;
                var remote = NormalizeFtpPath(remoteFolder);

                // Strict: only accept version.json in the version folder.
                var name = "version.json";
                var remotePath = NormalizeFtpPath(remote.TrimEnd('/') + "/" + name);
                var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + "_" + name);

                try
                {
                    Log($"Trying to read required version file: {remotePath}");

                    var ok = await DownloadRemoteFileSafeAsync(remotePath, tmp);
                    if (!ok)
                    {
                        // Try direct-download fallback (different FluentFTP overloads)
                        var ok2 = await TryDownloadFileDirectAsync(remotePath, tmp);
                        if (!ok2)
                        {
                            Log($"version.json not found or not retrievable at: {remotePath}");
                            return null;
                        }
                    }

                    var txt = File.ReadAllText(tmp, Encoding.UTF8).Trim();
                    if (string.IsNullOrWhiteSpace(txt))
                    {
                        Log($"Downloaded version.json is empty: {remotePath}");
                        return null;
                    }

                    try
                    {
                        using var doc = JsonDocument.Parse(txt);
                        if (doc.RootElement.ValueKind == JsonValueKind.Object)
                        {
                            if (doc.RootElement.TryGetProperty("version", out var verProp) ||
                                doc.RootElement.TryGetProperty("Version", out verProp))
                            {
                                var v = verProp.GetString();
                                if (!string.IsNullOrWhiteSpace(v))
                                {
                                    Log($"Found version in {remotePath}: {v}");
                                    return v.Trim();
                                }
                            }
                        }

                        Log($"version.json at {remotePath} does not contain a 'version' property.");
                        return null;
                    }
                    catch (Exception ex)
                    {
                        Log($"Failed parse JSON {remotePath}: {ex.Message}");
                        return null;
                    }
                }
                finally
                {
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                }
            }
            catch (Exception ex)
            {
                Log("ReadRemoteVersionFromFolderAsync lỗi: " + ex.Message);
                return null;
            }
        }

        // Insert this helper near other download helpers (e.g. after DownloadRemoteFileSafeAsync)
        private async Task<bool> TryDownloadFileDirectAsync(string remotePath, string localPath, CancellationToken ct = default)
        {
            try
            {
                var clientType = _client.GetType();
                var methods = clientType.GetMethods().Where(m => m.Name.StartsWith("DownloadFile", StringComparison.OrdinalIgnoreCase)).ToList();

                // prefer async overload with (localPath, remotePath, ...)
                var miAsync = methods.FirstOrDefault(m =>
                {
                    var p = m.GetParameters();
                    if (!m.Name.EndsWith("Async", StringComparison.OrdinalIgnoreCase)) return false;
                    return p.Length >= 2 && p[0].ParameterType == typeof(string) && p[1].ParameterType == typeof(string);
                });

                if (miAsync != null)
                {
                    var parameters = miAsync.GetParameters();
                    var args = new List<object> { localPath, remotePath };
                    for (int i = 2; i < parameters.Length; i++)
                    {
                        var pt = parameters[i].ParameterType;
                        if (pt == typeof(FluentFTP.FtpLocalExists)) args.Add(FluentFTP.FtpLocalExists.Overwrite);
                        else if (pt == typeof(FluentFTP.FtpVerify)) args.Add(FluentFTP.FtpVerify.None);
                        else if (pt == typeof(IProgress<FluentFTP.FtpProgress>)) args.Add(null);
                        else if (pt == typeof(CancellationToken)) args.Add(ct);
                        else args.Add(Type.Missing);
                    }

                    var task = (Task)miAsync.Invoke(_client, args.ToArray());
                    await task;

                    try
                    {
                        var fi = new FileInfo(localPath);
                        if (!fi.Exists || fi.Length == 0)
                        {
                            Log($"TryDownloadFileDirectAsync: downloaded but local file missing/zero: {localPath}");
                            return false;
                        }
                        Log($"TryDownloadFileDirectAsync success: {localPath} ({fi.Length} bytes)");
                        return true;
                    }
                    catch (Exception ex)
                    {
                        Log("TryDownloadFileDirectAsync: error checking local file: " + ex.Message);
                        return false;
                    }
                }

                // fallback to sync variant
                var miSync = methods.FirstOrDefault(m => !m.Name.EndsWith("Async", StringComparison.OrdinalIgnoreCase)
                                                        && m.GetParameters().Length >= 2
                                                        && m.GetParameters()[0].ParameterType == typeof(string)
                                                        && m.GetParameters()[1].ParameterType == typeof(string));
                if (miSync != null)
                {
                    miSync.Invoke(_client, new object[] { localPath, remotePath });
                    var fi = new FileInfo(localPath);
                    if (!fi.Exists || fi.Length == 0)
                    {
                        Log($"TryDownloadFileDirectAsync(sync) completed but local file missing/zero: {localPath}");
                        return false;
                    }
                    Log($"TryDownloadFileDirectAsync(sync) success: {localPath} ({fi.Length} bytes)");
                    return true;
                }

                Log("TryDownloadFileDirectAsync: no suitable DownloadFile method found on client.");
                return false;
            }
            catch (TargetInvocationException tie)
            {
                var inner = tie.InnerException ?? tie;
                Log("TryDownloadFileDirectAsync: FluentFTP invocation error: " + inner.Message);
                return false;
            }
            catch (Exception ex)
            {
                Log("TryDownloadFileDirectAsync error: " + ex.Message);
                return false;
            }
        }

        // Try to read a global `version.json` at the remoteRoot that enumerates all version folders and tokens.
        // Supported formats:
        // 1) { "versions": [ { "folder": "1.0.0", "version": "1.0.0" }, ... ] }
        // 2) [ { "folder": "1.0.0", "version": "1.0.0" }, ... ]
        // 3) { "1.0.0": "1.0.0", "1.1.0": "1.1.0" }  (object mapping folder -> token)
        // If found and valid, returns a list of (folderFullPath, token). Returns null if no global manifest found.
        private async Task<List<(string folder, string token)>?> ReadGlobalVersionManifestAsync(string remoteRoot)
        {
            if (string.IsNullOrWhiteSpace(remoteRoot)) return null;
            var remote = NormalizeFtpPath(remoteRoot);
            var name = "version.json";
            var remotePath = NormalizeFtpPath(remote.TrimEnd('/') + "/" + name);
            var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + "_" + name);

            try
            {
                Log($"Trying to read global version manifest: {remotePath}");

                // try the safe downloader first
                var ok = await DownloadRemoteFileSafeAsync(remotePath, tmp);
                if (!ok)
                {
                    // fallback direct
                    var ok2 = await TryDownloadFileDirectAsync(remotePath, tmp);
                    if (!ok2)
                    {
                        Log("No global version.json at " + remotePath);
                        return null;
                    }
                }

                var txt = File.ReadAllText(tmp, Encoding.UTF8).Trim();
                if (string.IsNullOrEmpty(txt))
                {
                    Log("Global version.json is empty: " + remotePath);
                    return null;
                }

                var list = new List<(string folder, string token)>();

                try
                {
                    using var doc = JsonDocument.Parse(txt);
                    var root = doc.RootElement;

                    // Case: object with "versions" array
                    if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("versions", out var versionsProp) && versionsProp.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var el in versionsProp.EnumerateArray())
                        {
                            if (el.ValueKind != JsonValueKind.Object) continue;
                            var folder = el.GetProperty("folder").GetString() ?? el.GetProperty("Folder").GetString() ?? "";
                            var token = el.GetProperty("version").GetString() ?? el.GetProperty("Version").GetString() ?? "";
                            if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(token)) continue;
                            var full = NormalizeFtpPath(remote.TrimEnd('/') + "/" + folder.Trim('/'));
                            list.Add((full, token.Trim()));
                        }
                        if (list.Count > 0) return list;
                    }

                    // NEW: Single-folder descriptor. Treat { "version": "x.y.z" } as "this folder's version"
                    if (root.ValueKind == JsonValueKind.Object &&
                        (root.TryGetProperty("version", out var singleVer) || root.TryGetProperty("Version", out singleVer)) &&
                        singleVer.ValueKind == JsonValueKind.String)
                    {
                        var token = singleVer.GetString()?.Trim();
                        if (!string.IsNullOrWhiteSpace(token))
                        {
                            Log("Global version.json describes current folder version (single-folder descriptor).");
                            return new List<(string folder, string token)> { (remote, token) };
                        }
                    }

                    // Case: root is array of objects
                    if (root.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var el in root.EnumerateArray())
                        {
                            if (el.ValueKind == JsonValueKind.Object)
                            {
                                var folder = el.TryGetProperty("folder", out var f1) ? f1.GetString() : null;
                                if (string.IsNullOrWhiteSpace(folder)) folder = el.TryGetProperty("Folder", out var f2) ? f2.GetString() : null;
                                var token = el.TryGetProperty("version", out var v1) ? v1.GetString() : null;
                                if (string.IsNullOrWhiteSpace(token)) token = el.TryGetProperty("Version", out var v2) ? v2.GetString() : null;
                                if (!string.IsNullOrWhiteSpace(folder) && !string.IsNullOrWhiteSpace(token))
                                {
                                    var full = NormalizeFtpPath(remote.TrimEnd('/') + "/" + folder.Trim('/'));
                                    list.Add((full, token.Trim()));
                                }
                            }
                        }
                        if (list.Count > 0) return list;
                    }

                    // Case: object mapping folder -> token (ignore keys like "version"/"versions")
                    if (root.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in root.EnumerateObject())
                        {
                            if (prop.NameEquals("version") || prop.NameEquals("Version") || prop.NameEquals("versions") || prop.NameEquals("Versions"))
                                continue;

                            var folder = prop.Name;
                            var token = prop.Value.GetString();
                            if (string.IsNullOrWhiteSpace(token)) continue;
                            var full = NormalizeFtpPath(remote.TrimEnd('/') + "/" + folder.Trim('/'));
                            list.Add((full, token.Trim()));
                        }
                        if (list.Count > 0) return list;
                    }
                }
                catch (Exception ex)
                {
                    Log("Failed parse global version.json: " + ex.Message);
                    return null;
                }

                Log("Global version.json parsed but contains no valid entries: " + remotePath);
                return null;
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }


        private async Task<int> CompareLocalVersionWithServerAsync(string remoteRoot)
        {
            // returns: -1 if local < remote (remote newer), 0 if equal, 1 if local > remote or unable to compare
            try
            {
                if (_client == null || !_client.IsConnected)
                {
                    MessageBox.Show("Chưa kết nối FTP. Vui lòng kết nối trước khi so sánh.");
                    return 1;
                }

                var localAppFolder = txtLocal?.Text?.Trim();
                if (string.IsNullOrEmpty(localAppFolder))
                {
                    MessageBox.Show("Chưa chọn thư mục local của ứng dụng. Vui lòng chọn Local folder.");
                    return 1;
                }

                Directory.CreateDirectory(localAppFolder);

                // 1) Read local version (this already checks version.json / version.txt / exe)
                var localVer = GetLocalVersionFromAppFolder(localAppFolder);
                Log($"Local version detected: {localVer}");

                // NEW: Require a valid local version, else stop
                if (string.IsNullOrWhiteSpace(localVer) || string.Equals(localVer, "0.0.0", StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show("Không xác định được phiên bản trong thư mục local. Dừng cập nhật.", "Cập nhật", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    SetStatus("Không xác định được phiên bản local.", true);
                    return 1;
                }

                // NEW: Require the selected root folder itself to expose a version (version.json), else stop
                var rootToken = await ReadRemoteVersionFromFolderAsync(remoteRoot);
                if (string.IsNullOrWhiteSpace(rootToken))
                {
                    MessageBox.Show($"Không tìm thấy file version.json hợp lệ trong thư mục gốc '{remoteRoot}'. Dừng cập nhật.", "Cập nhật", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    Log($"Root '{remoteRoot}' không có version.json hợp lệ.");
                    SetStatus("Không có version tại thư mục gốc.", true);
                    return 1;
                }

                SetStatus("Lấy danh sách phiên bản từ server...");

                // 2) Try global manifest first
                var global = await ReadGlobalVersionManifestAsync(remoteRoot);
                List<(string folder, string token)> versionMap = null;
                if (global != null && global.Count > 0)
                {
                    versionMap = global;
                    Log("Using global version.json to enumerate versions.");
                }
                else
                {
                    // fallback: discover folders and read per-folder version.json
                    var folders = await GetRemoteVersionFoldersAsync(remoteRoot);
                    var map = new List<(string folder, string token)>();
                    foreach (var f in folders)
                    {
                        try
                        {
                            var token = await ReadRemoteVersionFromFolderAsync(f);
                            if (!string.IsNullOrWhiteSpace(token))
                                map.Add((f, token));
                            else
                                Log($"Folder {f} không có version.json hoặc không hợp lệ.");
                        }
                        catch (Exception ex)
                        {
                            Log($"Lỗi đọc version ở {f}: {ex.Message}");
                        }
                    }
                    versionMap = map;
                }

                if (versionMap == null || versionMap.Count == 0)
                {
                    MessageBox.Show("Không tìm thấy phiên bản hợp lệ trên server.", "So sánh phiên bản", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    SetStatus("Không có phiên bản trên server.", true);
                    return 1;
                }

                // sort ascending and pick highest
                versionMap = versionMap
                    .OrderBy(v => v.token, Comparer<string>.Create((a, b) => CompareSemverVersions(a, b)))
                    .ToList();

                var highest = versionMap.Last();
                var remoteVer = highest.token;
                Log($"Highest remote version: {remoteVer} (folder: {highest.folder})");

                // 3) Compare
                var cmp = CompareSemverVersions(localVer, remoteVer);
                string msg;
                if (cmp < 0)
                {
                    msg = $"Local version: {localVer}\nRemote version: {remoteVer}\n\nCó bản mới trên server.";
                    SetStatus($"Có bản mới: {remoteVer}");
                }
                else if (cmp == 0)
                {
                    msg = $"Local version: {localVer}\nRemote version: {remoteVer}\n\nĐã là bản mới nhất.";
                    SetStatus("Đã là bản mới nhất.");
                }
                else
                {
                    msg = $"Local version: {localVer}\nRemote version: {remoteVer}\n\nLocal mới hơn remote (không cần cập nhật).";
                    SetStatus("Local mới hơn remote.");
                }

                var sb = new StringBuilder();
                sb.AppendLine(msg);
                sb.AppendLine();
                sb.AppendLine("Phiên bản trên server:");
                foreach (var v in versionMap)
                {
                    sb.AppendLine($" - {v.token}    ({v.folder})");
                }

                MessageBox.Show(sb.ToString(), "So sánh phiên bản", MessageBoxButtons.OK, MessageBoxIcon.Information);

                return cmp < 0 ? -1 : (cmp == 0 ? 0 : 1);
            }
            catch (Exception ex)
            {
                Log("CompareLocalVersionWithServerAsync error: " + ex.Message);
                return 1;
            }
        }


        // Insert helper to download only missing files from a version folder (used before applying a version)
        private async Task<(List<string> downloaded, List<string> failed, List<string> missing)> DownloadMissingFilesFromVersionAsync(
            string versionRemoteFolder, string localAppFolder, CancellationToken ct, IProgress<string>? status = null)
        {
            var downloaded = new List<string>();
            var failed = new List<string>();
            var missing = new List<string>();

            string localManifestTemp = null;
            try
            {
                localManifestTemp = await FindRemoteManifestInDirectorySafeAsync(versionRemoteFolder);
                if (string.IsNullOrEmpty(localManifestTemp) || !File.Exists(localManifestTemp))
                {
                    Log($"Không tìm thấy manifest trong {versionRemoteFolder}");
                    return (downloaded, failed, missing);
                }

                var expected = ParseManifest(localManifestTemp);
                if (expected == null || expected.Count == 0)
                {
                    Log("Manifest rỗng hoặc không hợp lệ: " + localManifestTemp);
                    return (downloaded, failed, missing);
                }

                // Skip manifest/checksum files themselves
                var expectedFiles = expected
                    .Where(kv => !IsManifestRelPath(kv.Key))
                    .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

                // Plan: only missing files
                var plan = new List<(string rel, string remote, string local, string expectedSha, long? size)>();
                foreach (var kv in expectedFiles.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                {
                    ct.ThrowIfCancellationRequested();

                    var rel = kv.Key.Replace('\\', '/').TrimStart('/');
                    var expectedSha = kv.Value;
                    var localPath = Path.Combine(localAppFolder, rel.Replace('/', Path.DirectorySeparatorChar));
                    var localDir = Path.GetDirectoryName(localPath) ?? localAppFolder;

                    if (File.Exists(localPath)) continue;

                    missing.Add(rel);
                    status?.Report("MISSING: " + rel);

                    Directory.CreateDirectory(localDir);

                    var remoteFile = NormalizeFtpPath(versionRemoteFolder.TrimEnd('/') + "/" + rel);
                    long? size = await TryGetRemoteFileSizeAsync(remoteFile);
                    plan.Add((rel, remoteFile, localPath, expectedSha, size));
                }

                var knownTotal = plan.Where(p => p.size.HasValue).Sum(p => p.size!.Value);
                Log($"Download-missing plan: {plan.Count} file(s). Known total bytes = {knownTotal}");
                var overall = new OverallTransfer(this, knownTotal, plan.Count);

                int done = 0;
                foreach (var p in plan)
                {
                    ct.ThrowIfCancellationRequested();

                    var tmpLocal = p.local + ".tmp";
                    try { if (File.Exists(tmpLocal)) File.Delete(tmpLocal); } catch { }

                    status?.Report($"Downloading ({++done}/{plan.Count}): {p.rel}");
                    Log($"Download missing: {p.remote} -> {tmpLocal}");

                    var perFile = overall.CreatePerFileProgress(p.remote, p.size);
                    var ok = await DownloadRemoteFileSafeAsync(p.remote, tmpLocal, ct, perFile);
                    if (!ok)
                    {
                        Log($"Tải file thiếu thất bại: {p.remote}");
                        try { if (File.Exists(tmpLocal)) File.Delete(tmpLocal); } catch { }
                        failed.Add(p.rel);
                        continue;
                    }

                    try
                    {
                        var downloadedSha = await ComputeFileSha256Async(tmpLocal, null, ct);
                        if (!string.Equals(downloadedSha, p.expectedSha, StringComparison.OrdinalIgnoreCase))
                        {
                            Log($"SHA mismatch for missing file {p.rel}: expected {p.expectedSha} got {downloadedSha}");
                            try { if (File.Exists(tmpLocal)) File.Delete(tmpLocal); } catch { }
                            failed.Add(p.rel);
                            continue;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log("Lỗi verify downloaded sha cho missing file: " + ex.Message);
                        try { if (File.Exists(tmpLocal)) File.Delete(tmpLocal); } catch { }
                        failed.Add(p.rel);
                        continue;
                    }

                    try
                    {
                        if (File.Exists(p.local)) File.Delete(p.local);
                        File.Move(tmpLocal, p.local);
                        downloaded.Add(p.rel);
                        status?.Report("Downloaded: " + p.rel);
                        Log("Downloaded missing file: " + p.local);
                    }
                    catch (Exception ex)
                    {
                        Log($"Không thể lưu file '{p.local}': {ex.Message}");
                        try { if (File.Exists(tmpLocal)) File.Delete(tmpLocal); } catch { }
                        failed.Add(p.rel);
                    }
                }

                return (downloaded, failed, missing);
            }
            finally
            {
                try { if (!string.IsNullOrEmpty(localManifestTemp) && File.Exists(localManifestTemp)) File.Delete(localManifestTemp); } catch { }
            }
        }
        // Find the remote folder that matches a given version token.
        // Strategy:
        // 1) If remoteRoot itself has version == targetVersion, use it.
        // 2) Else try parent of remoteRoot: read global manifest; if found, pick entry whose token == targetVersion.
        // 3) Else scan folders under parent and read their version.json to find a match.
        private async Task<string?> FindRemoteFolderForVersionAsync(string remoteRoot, string targetVersion)
        {
            if (string.IsNullOrWhiteSpace(remoteRoot) || string.IsNullOrWhiteSpace(targetVersion))
                return null;

            var remote = NormalizeFtpPath(remoteRoot);

            // 1) Check remoteRoot itself
            try
            {
                var verHere = await ReadRemoteVersionFromFolderAsync(remote);
                if (!string.IsNullOrWhiteSpace(verHere) && CompareSemverVersions(verHere, targetVersion) == 0)
                    return remote;
            }
            catch { /* ignore */ }

            // 2) Try parent with global manifest
            var parent = GetFtpParent(remote);
            try
            {
                var map = await ReadGlobalVersionManifestAsync(parent);
                if (map != null && map.Count > 0)
                {
                    var match = map.FirstOrDefault(x => CompareSemverVersions(x.token, targetVersion) == 0);
                    if (!string.IsNullOrWhiteSpace(match.folder)) return NormalizeFtpPath(match.folder);
                }
            }
            catch { /* ignore */ }

            // 3) Fallback: scan folders under parent
            try
            {
                var folders = await GetRemoteVersionFoldersAsync(parent);
                foreach (var f in folders)
                {
                    try
                    {
                        var tok = await ReadRemoteVersionFromFolderAsync(f);
                        if (!string.IsNullOrWhiteSpace(tok) && CompareSemverVersions(tok, targetVersion) == 0)
                            return NormalizeFtpPath(f);
                    }
                    catch { /* ignore per folder */ }
                }
            }
            catch { /* ignore */ }

            return null;
        }


        private async Task CompareAndOfferFixMissingSameVersionAsync(string remoteRoot)
        {
            if (_client == null || !_client.IsConnected)
            {
                MessageBox.Show("Please connect to FTP first.");
                return;
            }

            var localAppFolder = txtLocal?.Text?.Trim();
            if (string.IsNullOrWhiteSpace(localAppFolder))
            {
                MessageBox.Show("Please choose Local folder first.");
                return;
            }
            Directory.CreateDirectory(localAppFolder);

            var localVer = GetLocalVersionFromAppFolder(localAppFolder);
            if (string.IsNullOrWhiteSpace(localVer) || localVer == "0.0.0")
            {
                MessageBox.Show("Could not detect local version. Ensure version.txt/version.json or exe version exists.");
                return;
            }

            SetStatus($"Finding remote folder for version {localVer} ...");
            Log($"Find remote folder for same version = {localVer}");

            var matchedRemote = await FindRemoteFolderForVersionAsync(remoteRoot, localVer);
            if (string.IsNullOrWhiteSpace(matchedRemote))
            {
                MessageBox.Show($"No folder for version {localVer} found on server under '{remoteRoot}'.", "Same-version check", MessageBoxButtons.OK, MessageBoxIcon.Information);
                SetStatus("No matching version folder on server.", true);
                return;
            }

            Log($"Matched remote folder for version {localVer}: {matchedRemote}");
            SetStatus($"Reading manifest in {matchedRemote} ...");

            string remoteManifestTemp = null;
            try
            {
                remoteManifestTemp = await FindRemoteManifestInDirectorySafeAsync(matchedRemote);
                if (string.IsNullOrEmpty(remoteManifestTemp) || !File.Exists(remoteManifestTemp))
                {
                    MessageBox.Show($"No manifest (checksums.txt/manifest.txt) found in {matchedRemote}. Cannot compare.", "Same-version check", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    SetStatus("Missing manifest on server.", true);
                    return;
                }

                // Verify current local app folder against remote manifest
                var verifyProgress = new Progress<(string relativePath, int state)>(s =>
                {
                    try
                    {
                        SafeInvoke(() =>
                        {
                            if (!string.IsNullOrEmpty(s.relativePath))
                                toolStripStatusLabelStatus.Text = $"{s.relativePath} - {(s.state == 2 ? "OK" : s.state == 1 ? "MISMATCH" : s.state == 3 ? "MISSING" : s.state == 4 ? "EXTRA" : "...")}";
                        });
                    }
                    catch { }
                });

                _cts?.Dispose();
                _cts = new CancellationTokenSource();
                var ct = _cts.Token;

                SetUiBusy(true);
                var prev = Cursor;
                Cursor = Cursors.WaitCursor;

                try
                {
                    var (missing, mismatched, extra) = await VerifyDirectoryAgainstManifestAsync(localAppFolder, remoteManifestTemp, verifyProgress, ct);

                    var sb = new StringBuilder();
                    sb.AppendLine($"Compare same version {localVer}");
                    sb.AppendLine($"Remote folder: {matchedRemote}");
                    sb.AppendLine($"Missing: {missing.Count}    Mismatched: {mismatched.Count}    Extra: {extra.Count}");
                    if (mismatched.Any())
                    {
                        sb.AppendLine();
                        sb.AppendLine("Some mismatched files (up to 50):");
                        foreach (var m in mismatched.Take(50)) sb.AppendLine(" - " + m);
                    }
                    if (extra.Any())
                    {
                        sb.AppendLine();
                        sb.AppendLine("Some extra files (up to 50):");
                        foreach (var m in extra.Take(50)) sb.AppendLine(" - " + m);
                    }

                    // Auto-fix: if not matching, download ONLY missing files (no prompt)
                    if (missing.Count > 0)
                    {
                        sb.AppendLine();
                        sb.AppendLine($"Auto-fixing: downloading {missing.Count} missing file(s) ...");
                        Log($"Auto download {missing.Count} missing files for version {localVer}.");

                        var prog = new Progress<string>(s =>
                        {
                            try { toolStripStatusLabelStatus.Text = s; } catch { }
                            Log(s);
                        });

                        var (downloaded, failed, _) = await DownloadMissingFilesFromVersionAsync(matchedRemote, localAppFolder, ct, prog);

                        sb.AppendLine($"Downloaded: {downloaded.Count}    Failed: {failed.Count}");
                        if (failed.Any())
                        {
                            sb.AppendLine();
                            sb.AppendLine("Failed (up to 50):");
                            foreach (var f in failed.Take(50)) sb.AppendLine(" - " + f);
                        }

                        SetStatus(failed.Any() ? "Downloaded missing files with some failures." : "Downloaded missing files successfully.", failed.Any());
                    }
                    else
                    {
                        sb.AppendLine();
                        sb.AppendLine("No missing files. Nothing to download.");
                        SetStatus("Same-version check: no missing files.");
                    }

                    MessageBox.Show(sb.ToString(), "Same-version manifest comparison", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (OperationCanceledException)
                {
                    Log("Same-version comparison canceled.");
                    SetStatus("Canceled.", true);
                }
                finally
                {
                    Cursor = prev;
                    SetUiBusy(false);
                    _cts?.Dispose();
                    _cts = null;
                }
            }
            finally
            {
                try { if (!string.IsNullOrEmpty(remoteManifestTemp) && File.Exists(remoteManifestTemp)) File.Delete(remoteManifestTemp); } catch { }
            }
        }

        private async Task<bool> DownloadByStreamFallbackAsync(string remotePath, string localPath, CancellationToken ct)
        {
            async Task<Stream?> OpenReadAsyncWithPath(string path)
            {
                var clientType = _client.GetType();
                var miOpenReadAsync = clientType.GetMethod(
                    "OpenReadAsync",
                    new Type[] { typeof(string), typeof(FluentFTP.FtpDataType), typeof(long), typeof(CancellationToken) });

                if (miOpenReadAsync != null)
                {
                    var task = (Task)miOpenReadAsync.Invoke(_client, new object[] { path, FluentFTP.FtpDataType.Binary, 0L, ct });
                    await task;
                    return task.GetType().GetProperty("Result")?.GetValue(task) as Stream;
                }

                var miOpenRead = clientType.GetMethod(
                    "OpenRead",
                    new Type[] { typeof(string), typeof(FluentFTP.FtpDataType), typeof(long) });

                if (miOpenRead != null)
                {
                    return miOpenRead.Invoke(_client, new object[] { path, FluentFTP.FtpDataType.Binary, 0L }) as Stream;
                }

                return null;
            }

            async Task<bool> CopyStreamAsync(Stream inStream)
            {
                await using (inStream)
                await using (var outFs = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    var buffer = new byte[81920];
                    long total = 0;

                    // NEW: throttled streaming debug logs
                    DateTime lastLog = DateTime.MinValue;
                    long lastBytes = 0;

                    var swLocal = Stopwatch.StartNew();

                    while (true)
                    {
                        ct.ThrowIfCancellationRequested();
                        var n = await inStream.ReadAsync(buffer, 0, buffer.Length, ct);
                        if (n == 0) break;
                        await outFs.WriteAsync(buffer, 0, n, ct);
                        total += n;

                        var now = DateTime.UtcNow;
                        if ((now - lastLog).TotalSeconds >= 2.0)
                        {
                            var delta = total - lastBytes;
                            var secs = Math.Max(0.001, swLocal.Elapsed.TotalSeconds);
                            var curSpeed = delta / Math.Max(0.001, (now - lastLog).TotalSeconds);
                            Log($"Stream fallback: wrote {FormatBytes(total)}  speed {(curSpeed > 0 ? FormatBytes((long)curSpeed) + "/s" : "--")}");
                            lastLog = now;
                            lastBytes = total;
                        }
                    }
                    await outFs.FlushAsync(ct);
                    if (total <= 0)
                    {
                        Log("DownloadByStreamFallbackAsync: tải xong nhưng dữ liệu = 0 byte.");
                        return false;
                    }
                    Log($"DownloadByStreamFallbackAsync: tải thành công {total} bytes -> {localPath}");
                    return true;
                }
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(localPath) ?? ".");

                // 1) Try absolute path first
                var abs = NormalizeFtpPath(remotePath);
                var s1 = await OpenReadAsyncWithPath(abs);
                if (s1 != null)
                {
                    if (await CopyStreamAsync(s1)) return true;
                    try { if (File.Exists(localPath)) File.Delete(localPath); } catch { }
                }

                // 2) Try resolved path from listing
                var resolved = await ResolveRemoteFilePathAsync(abs);
                if (!string.IsNullOrWhiteSpace(resolved) && !string.Equals(resolved, abs, StringComparison.OrdinalIgnoreCase))
                {
                    var s2 = await OpenReadAsyncWithPath(resolved);
                    if (s2 != null)
                    {
                        if (await CopyStreamAsync(s2)) return true;
                        try { if (File.Exists(localPath)) File.Delete(localPath); } catch { }
                    }
                }

                // 3) Try CWD to parent and open by filename (some servers require relative RETR)
                var parent = GetFtpParent(abs);
                var name = Path.GetFileName(abs);
                try
                {
                    var clientType = _client.GetType();
                    var miCwdAsync = clientType.GetMethod("SetWorkingDirectoryAsync", new[] { typeof(string) });
                    if (miCwdAsync != null)
                    {
                        var t = (Task)miCwdAsync.Invoke(_client, new object[] { parent });
                        await t;
                    }
                    else
                    {
                        var miCwd = clientType.GetMethod("SetWorkingDirectory", new[] { typeof(string) });
                        miCwd?.Invoke(_client, new object[] { parent });
                    }
                    Log($"DownloadByStreamFallbackAsync: switched CWD -> {parent}, try RETR '{name}'");
                    var s3 = await OpenReadAsyncWithPath(name);
                    if (s3 != null)
                    {
                        if (await CopyStreamAsync(s3)) return true;
                        try { if (File.Exists(localPath)) File.Delete(localPath); } catch { }
                    }
                }
                catch (Exception exCwd)
                {
                    Log("DownloadByStreamFallbackAsync CWD fallback lỗi: " + exCwd.Message);
                }

                return false;
            }
            catch (OperationCanceledException)
            {
                Log("DownloadByStreamFallbackAsync: bị hủy bởi người dùng.");
                try { if (File.Exists(localPath)) File.Delete(localPath); } catch { }
                throw;
            }
            catch (Exception ex)
            {
                Log("DownloadByStreamFallbackAsync lỗi: " + ex.Message);
                try { if (File.Exists(localPath)) File.Delete(localPath); } catch { }
                return false;
            }
        }


        // Try to resolve a remote file to a concrete path from parent listing (case-insensitive, trims oddities)
        private async Task<string?> ResolveRemoteFilePathAsync(string remotePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(remotePath)) return null;
                var norm = NormalizeFtpPath(remotePath);
                var parent = GetFtpParent(norm);
                var target = Path.GetFileName(norm)?.Trim();
                if (string.IsNullOrWhiteSpace(target)) return null;

                var listing = await GetRemoteListingAsync(parent);
                foreach (var item in listing)
                {
                    try
                    {
                        var name = item.GetType().GetProperty("Name")?.GetValue(item)?.ToString()?.Trim();
                        var full = item.GetType().GetProperty("FullName")?.GetValue(item)?.ToString()?.Trim();
                        if (string.IsNullOrWhiteSpace(name)) continue;

                        if (string.Equals(name, target, StringComparison.OrdinalIgnoreCase) ||
                            (!string.IsNullOrWhiteSpace(full) && full.Replace('\\', '/').EndsWith("/" + target, StringComparison.OrdinalIgnoreCase)))
                        {
                            var resolved = !string.IsNullOrWhiteSpace(full)
                                ? NormalizeFtpPath(full)
                                : NormalizeFtpPath(parent.TrimEnd('/') + "/" + name);
                            Log($"ResolveRemoteFilePathAsync: '{remotePath}' -> '{resolved}' (via listing)");
                            return resolved;
                        }
                    }
                    catch { /* ignore this entry */ }
                }
            }
            catch (Exception ex)
            {
                Log("ResolveRemoteFilePathAsync lỗi: " + ex.Message);
            }
            return null;
        }


        // Force binary transfer mode on the client (works across FluentFTP versions via reflection)
        private void ForceBinaryTransferMode(FluentFTP.FtpClient client)
        {
            try
            {
                var t = client.GetType();
                var p = t.GetProperty("DownloadDataType");
                if (p != null && p.CanWrite) p.SetValue(client, FluentFTP.FtpDataType.Binary);
            }
            catch { }
            try
            {
                var t = client.GetType();
                var p = t.GetProperty("UploadDataType");
                if (p != null && p.CanWrite) p.SetValue(client, FluentFTP.FtpDataType.Binary);
            }
            catch { }
            try
            {
                var t = client.GetType();
                var p = t.GetProperty("DataType");
                if (p != null && p.CanWrite) p.SetValue(client, FluentFTP.FtpDataType.Binary);
            }
            catch { }
        }

        private async void button2_Click(object sender, EventArgs e)
        {
            // Determine remote root ...
            string remoteToUse = null;
            try
            {
                var currentRemoteBase = NormalizeFtpPath((txtRemote?.Text ?? "/").Trim());
                if (string.IsNullOrEmpty(currentRemoteBase)) currentRemoteBase = "/";

                if (lvFiles.SelectedItems.Count > 0)
                {
                    var sel = lvFiles.SelectedItems[0];
                    string name = sel.Text?.Trim() ?? "";

                    string fullFromTag = null;
                    object typeFromTag = null;
                    if (sel.Tag != null)
                    {
                        try
                        {
                            var t = sel.Tag;
                            var fullProp = t.GetType().GetProperty("FullName");
                            var nameProp = t.GetType().GetProperty("Name");
                            var typeProp = t.GetType().GetProperty("Type");
                            fullFromTag = fullProp?.GetValue(t)?.ToString();
                            var nm = nameProp?.GetValue(t)?.ToString();
                            if (!string.IsNullOrEmpty(nm)) name = nm;
                            typeFromTag = typeProp?.GetValue(t);
                        }
                        catch { }
                    }

                    string candidateFull = !string.IsNullOrEmpty(fullFromTag)
                        ? NormalizeFtpPath(fullFromTag)
                        : NormalizeFtpPath((currentRemoteBase == "/" ? "" : currentRemoteBase.TrimEnd('/')) + "/" + name);

                    bool isDir = false;
                    bool typeDecided = false;
                    if (typeFromTag != null)
                    {
                        try
                        {
                            var tstr = typeFromTag.ToString();
                            if (!string.IsNullOrEmpty(tstr))
                            {
                                if (tstr.IndexOf("dir", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    tstr.IndexOf("directory", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    tstr.IndexOf("folder", StringComparison.OrdinalIgnoreCase) >= 0)
                                { isDir = true; typeDecided = true; }
                                else if (tstr.IndexOf("file", StringComparison.OrdinalIgnoreCase) >= 0)
                                { isDir = false; typeDecided = true; }
                            }
                        }
                        catch { }
                    }
                    if (!typeDecided)
                    {
                        try { isDir = await DecideIsDirectoryAsync(sel.Tag, candidateFull); }
                        catch { isDir = true; }
                    }

                    remoteToUse = isDir ? candidateFull : GetFtpParent(candidateFull);
                }

                if (string.IsNullOrEmpty(remoteToUse))
                {
                    var r = (txtRemote?.Text ?? "/").Trim();
                    if (string.IsNullOrEmpty(r)) r = "/";
                    remoteToUse = NormalizeFtpPath(r);
                }

                Log($"button2_Click: using remote root for updates: {remoteToUse}");

                // Compare SAME VERSION manifest first and offer to download only missing files.
                await CompareAndOfferFixMissingSameVersionAsync(remoteToUse);

                // Compare local vs highest remote to decide if sequential update is needed
                int cmp = 1;
                try
                {
                    cmp = await CompareLocalVersionWithServerAsync(remoteToUse);
                }
                catch (Exception exComp)
                {
                    Log("CompareLocalVersionWithServerAsync error: " + exComp.Message);
                    cmp = 1;
                }

                if (cmp >= 0)
                {
                    Log("Local is up-to-date or newer; skipping sequential update.");
                    return;
                }

                // AUTO: no confirmation, proceed directly
                Log("Auto proceeding with sequential update...");
                await CheckAndPerformSequentialVersionUpdatesAsync(remoteToUse);
            }
            catch (Exception ex)
            {
                Log("button2_Click error: " + ex.Message);
                MessageBox.Show("Lỗi khi bắt đầu cập nhật: " + ex.Message, "Lỗi", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        // Overall progress aggregator for multi-file transfers
        // Overall progress aggregator for multi-file transfers (bytes-based when possible, otherwise items-based)
        private sealed class OverallTransfer
        {
            private readonly Form1 _owner;
            public long TotalBytes { get; }           // initial known total (may be 0)
            public int TotalItems { get; }            // number of files in the plan
            private long _completed;                  // aggregated transferred bytes
            private readonly Stopwatch _sw = Stopwatch.StartNew();
            private readonly object _lock = new();
            private readonly Dictionary<string, long> _perFileLast = new(StringComparer.OrdinalIgnoreCase);

            // Throttled logging state
            private DateTime _lastOverallLog = DateTime.MinValue;
            private int _lastOverallPct = -1;
            private readonly Dictionary<string, DateTime> _fileStartAt = new(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, DateTime> _fileLastLogAt = new(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, int> _fileLastPct = new(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, long?> _fileSizeHints = new(StringComparer.OrdinalIgnoreCase); // may be filled later from progress.TotalBytes

            public OverallTransfer(Form1 owner, long totalBytes, int totalItems)
            {
                _owner = owner;
                TotalBytes = totalBytes < 0 ? 0 : totalBytes;
                TotalItems = Math.Max(0, totalItems);
            }

            public IProgress<FluentFTP.FtpProgress> CreatePerFileProgress(string fileKey, long? fileSizeHint = null)
            {
                // keep size hint for ETA/bytes-based %
                lock (_lock)
                {
                    if (!_fileSizeHints.ContainsKey(fileKey)) _fileSizeHints[fileKey] = fileSizeHint;
                }

                return new Progress<FluentFTP.FtpProgress>(p =>
                {
                    try
                    {
                        long cur = 0;
                        double speed = 0;
                        int pct = 0;
                        long totalOfThisFile = 0;

                        try { cur = (long)p.TransferredBytes; } catch { /* ignore */ }
                        try { speed = p.TransferSpeed; } catch { /* ignore */ }
                        try { pct = (int)Math.Min(100, Math.Max(0, p.Progress)); } catch { /* ignore */ }
                        try { totalOfThisFile = _fileSizeHints.TryGetValue(fileKey, out var hint) && hint.HasValue ? hint.Value : 0; } catch { /* ignore */ }
                        bool shouldLogNow = false;
                        string logLine = null;

                        lock (_lock)
                        {
                            // If FluentFTP provides TotalBytes for this file, store it as hint
                            if (totalOfThisFile > 0)
                            {
                                _fileSizeHints[fileKey] = totalOfThisFile;
                            }

                            // Aggregate new bytes into _completed
                            if (!_perFileLast.TryGetValue(fileKey, out var last)) last = 0;
                            var delta = Math.Max(0, cur - last);
                            _perFileLast[fileKey] = cur;
                            _completed += delta;

                            // Per-file start
                            if (!_fileStartAt.ContainsKey(fileKey))
                            {
                                _fileStartAt[fileKey] = DateTime.UtcNow;
                                _fileLastLogAt[fileKey] = DateTime.MinValue;
                                _fileLastPct[fileKey] = -1;

                                var sz = _fileSizeHints.TryGetValue(fileKey, out var hint) ? hint : null;
                                _owner.Log($"Start: {fileKey} size={(sz.HasValue ? _owner.FormatBytes(sz.Value) : "?")}");
                            }

                            // throttle: every 10% or 2s or 100%
                            var lastPct = _fileLastPct[fileKey];
                            var lastAt = _fileLastLogAt[fileKey];
                            var now = DateTime.UtcNow;
                            if (pct >= Math.Max(0, lastPct + 10) || (now - lastAt).TotalSeconds >= 2.0 || pct == 100)
                            {
                                _fileLastPct[fileKey] = pct;
                                _fileLastLogAt[fileKey] = now;

                                string speedText = speed > 0 ? _owner.FormatBytes((long)speed) + "/s" : "--";
                                string eta = "--:--";
                                var hintSz = _fileSizeHints.TryGetValue(fileKey, out var hs) ? hs : null;
                                if (hintSz.HasValue && hintSz.Value > 0 && speed > 0)
                                {
                                    var remain = Math.Max(0, hintSz.Value - cur);
                                    eta = _owner.FormatSeconds(remain / speed);
                                }
                                logLine = $"File: {fileKey}  {pct}%  ({_owner.FormatBytes(cur)})  {speedText}  ETA {eta}";
                                shouldLogNow = true;

                                if (pct >= 100)
                                {
                                    var secs = Math.Max(0.001, (now - _fileStartAt[fileKey]).TotalSeconds);
                                    var avg = cur / secs;
                                    _owner.Log($"Done: {fileKey}  {_owner.FormatBytes(cur)} in {secs:F1}s  avg {_owner.FormatBytes((long)avg)}/s");
                                }
                            }

                            UpdateUi_NoLock();
                        }

                        if (shouldLogNow && !string.IsNullOrEmpty(logLine))
                        {
                            _owner.Log(logLine);
                        }
                    }
                    catch { /* ignore */ }
                });
            }

            public void AddCompletedBytes(long bytes)
            {
                lock (_lock)
                {
                    _completed += Math.Max(0, bytes);
                    UpdateUi_NoLock();
                }
            }

            private void UpdateUi_NoLock()
            {
                // Prefer a bytes-based percentage with a dynamic total (sum of hints) if initial total is 0
                long sumHints = 0;
                if (_fileSizeHints.Count > 0)
                {
                    foreach (var v in _fileSizeHints.Values)
                        if (v.HasValue && v.Value > 0) sumHints += v.Value;
                }
                long totalForUi = TotalBytes > 0 ? TotalBytes : sumHints;

                double pctBytes = totalForUi > 0 ? (_completed * 100.0 / totalForUi) : -1;

                // Items-based fallback: average of per-file percentages across TotalItems
                double pctItems = -1;
                if (TotalItems > 0)
                {
                    long sumPct = 0;
                    foreach (var v in _fileLastPct.Values) sumPct += Math.Max(0, Math.Min(100, v));
                    // files not started implicitly count as 0%
                    pctItems = sumPct * 1.0 / TotalItems;
                }

                double chosenPct = pctBytes >= 0 ? pctBytes : (pctItems >= 0 ? pctItems : 0);
                int pct = (int)Math.Min(100, Math.Max(0, Math.Round(chosenPct)));

                // Speed/ETA (bytes-based when possible)
                double seconds = Math.Max(0.001, _sw.Elapsed.TotalSeconds);
                double speed = _completed / seconds; // bytes/sec
                string speedText = speed > 0 ? $"{_owner.FormatBytes((long)speed)}/s" : "--";

                string eta = "--:--";
                if (totalForUi > 0 && speed > 0)
                {
                    var remaining = Math.Max(0, totalForUi - _completed);
                    eta = _owner.FormatSeconds(remaining / speed);
                }

                _owner.SafeInvoke(() =>
                {
                    try
                    {
                        if (_owner.progressBar != null)
                        {
                            _owner.progressBar.Minimum = 0;
                            _owner.progressBar.Maximum = 100;
                            _owner.progressBar.Style = ProgressBarStyle.Blocks;
                            _owner.progressBar.Value = pct;
                            _owner.progressBar.Visible = true;
                        }
                    }
                    catch { }
                    try
                    {
                        if (_owner.lblSpeed2 != null)
                        {
                            var totalText = totalForUi > 0 ? _owner.FormatBytes(totalForUi) : "?";
                            _owner.lblSpeed2.Text = $" {speedText}  ETA: {eta}  ({_owner.FormatBytes(_completed)}/{totalText})";
                        }
                    }
                    catch { }
                });

                // Throttled overall debug log (every 5% or 2s or 100%)
                var now = DateTime.UtcNow;
                if (pct >= Math.Max(0, _lastOverallPct + 5) || (now - _lastOverallLog).TotalSeconds >= 2.0 || pct == 100)
                {
                    _lastOverallPct = pct;
                    _lastOverallLog = now;
                    var totalText = totalForUi > 0 ? _owner.FormatBytes(totalForUi) : "?";
                    _owner.Log($"Overall: {pct}%  ({_owner.FormatBytes(_completed)}/{totalText})  {speedText}  ETA {eta}");
                }
            }
        }



        // Plan items for selection-based downloads (files + directories)
        private async Task<List<(string remote, string local, long? size)>> PlanDownloadsFromSelectionAsync(
            IEnumerable<ListViewItem> selectedItems, string currentRemoteBase, string localBase, CancellationToken ct)
        {
            var plan = new List<(string remote, string local, long? size)>();
            Directory.CreateDirectory(localBase);

            foreach (var item in selectedItems)
            {
                ct.ThrowIfCancellationRequested();

                string candidate = null;
                string name = item.Text;
                if (item.Tag != null)
                {
                    var t = item.Tag;
                    var fullProp = t.GetType().GetProperty("FullName");
                    var nameProp = t.GetType().GetProperty("Name");
                    var full = fullProp?.GetValue(t)?.ToString();
                    var nm = nameProp?.GetValue(t)?.ToString();
                    candidate = full ?? nm;
                    if (!string.IsNullOrEmpty(nm)) name = nm;
                }

                name = SanitizeRemoteName(name);
                var foundRemote = await FindExistingRemoteFileAsync(candidate, currentRemoteBase, name);
                if (foundRemote == null)
                {
                    Log($"Không tìm thấy remote cho: {name} (bỏ qua)");
                    continue;
                }
                foundRemote = NormalizeFtpPath(foundRemote);

                bool isDir = await DecideIsDirectoryAsync(item.Tag, foundRemote);
                var localPathBase = Path.Combine(localBase, name);

                if (!isDir)
                {
                    var size = await TryGetRemoteFileSizeAsync(foundRemote);
                    plan.Add((foundRemote, localPathBase, size));
                }
                else
                {
                    var subtree = await EnumerateRemoteFilesRecursiveAsync(foundRemote, localPathBase, ct);
                    plan.AddRange(subtree);
                }
            }

            return plan;
        }

        // Enumerate files recursively under a remote directory and map to local paths
        private async Task<List<(string remote, string local, long? size)>> EnumerateRemoteFilesRecursiveAsync(
            string remoteDir, string localDir, CancellationToken ct)
        {
            var list = new List<(string remote, string local, long? size)>();
            Directory.CreateDirectory(localDir);

            var entries = await GetRemoteListingAsync(remoteDir);
            foreach (var it in entries)
            {
                ct.ThrowIfCancellationRequested();

                var info = InspectListingItem(it);
                string rawName = info.Name;
                string rawFull = info.FullName;

                string name = SanitizeRemoteName(rawName ?? "");
                if (string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(rawFull))
                    name = SanitizeRemoteName(Path.GetFileName(rawFull));
                if (string.IsNullOrEmpty(name)) continue;

                string childRemote = !string.IsNullOrEmpty(rawFull) ? rawFull.Trim() : (remoteDir.TrimEnd('/') + "/" + name);
                childRemote = childRemote.Replace('\\', '/');
                string childLocal = Path.Combine(localDir, name);

                bool isDir = await DecideIsDirectoryAsync(it, childRemote);
                if (isDir)
                {
                    var sub = await EnumerateRemoteFilesRecursiveAsync(childRemote, childLocal, ct);
                    list.AddRange(sub);
                }
                else
                {
                    var size = await TryGetRemoteFileSizeAsync(childRemote);
                    list.Add((NormalizeFtpPath(childRemote), childLocal, size));
                }
            }

            return list;
        }
    }
}
