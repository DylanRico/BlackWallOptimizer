using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Blackwall
{
    public sealed class OtaRelease
    {
        public string Version, Url, Hash, Notes;
        public long Size;
    }

    public static class OtaProtocol
    {
        public static JavaScriptSerializer Json { get { return new JavaScriptSerializer { MaxJsonLength = 262144, RecursionLimit = 12 }; } }
        public static Version ParseVersion(string value)
        {
            Version version;
            if (value == null || !Regex.IsMatch(value, @"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$") || !Version.TryParse(value, out version) || version.Major > 65534 || version.Minor > 65534 || version.Build > 65534)
                throw new InvalidDataException("Versión no válida; se requiere mayor.menor.parche.");
            return version;
        }
        public static Uri Https(string value)
        {
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
                throw new InvalidDataException("Las actualizaciones requieren una URL HTTPS sin credenciales ni fragmento.");
            return uri;
        }
        public static string HashFile(string file)
        {
            using (var stream = File.OpenRead(file)) using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
        public static string PublicKey()
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("update-public-key.xml"))
            {
                if (stream == null) throw new InvalidDataException("Esta compilación no incorpora una clave OTA de confianza.");
                using (var reader = new StreamReader(stream)) return reader.ReadToEnd();
            }
        }
        private static void Keys(Dictionary<string, object> value, params string[] names)
        {
            if (value == null || value.Count != names.Length) throw new InvalidDataException("Estructura de manifiesto no válida.");
            foreach (string name in names) if (!value.ContainsKey(name)) throw new InvalidDataException("Falta el campo " + name);
        }
        public static OtaRelease Verify(byte[] envelope, string publicKey)
        {
            if (envelope.Length > 262144) throw new InvalidDataException("Manifiesto demasiado grande.");
            var outer = Json.Deserialize<Dictionary<string, object>>(new UTF8Encoding(false, true).GetString(envelope));
            Keys(outer, "payload", "signature");
            byte[] payload = Convert.FromBase64String((string)outer["payload"]);
            byte[] signature = Convert.FromBase64String((string)outer["signature"]);
            if (payload.Length > 65536) throw new InvalidDataException("Contenido del manifiesto demasiado grande.");
            using (var rsa = new RSACryptoServiceProvider())
            {
                rsa.PersistKeyInCsp = false;
                rsa.FromXmlString(publicKey);
                if (rsa.KeySize < 3072 || !rsa.PublicOnly || !rsa.VerifyData(payload, "SHA256", signature)) throw new InvalidDataException("Firma OTA no válida.");
            }
            var data = Json.Deserialize<Dictionary<string, object>>(new UTF8Encoding(false, true).GetString(payload));
            Keys(data, "schemaVersion", "appVersion", "binary", "changelog");
            if (!(data["schemaVersion"] is int) || (int)data["schemaVersion"] != 1) throw new InvalidDataException("Formato OTA no compatible.");
            string version = (string)data["appVersion"];
            ParseVersion(version);
            var binary = data["binary"] as Dictionary<string, object>;
            Keys(binary, "url", "sha256", "size");
            string hash = (string)binary["sha256"];
            if (!Regex.IsMatch(hash, "^[a-f0-9]{64}$")) throw new InvalidDataException("SHA-256 obligatorio no válido.");
            if (!(binary["size"] is int) && !(binary["size"] is long)) throw new InvalidDataException("Tamaño no válido.");
            long size = Convert.ToInt64(binary["size"], CultureInfo.InvariantCulture);
            if (size < 40960 || size > 104857600) throw new InvalidDataException("Tamaño de ejecutable no permitido.");
            string url = Https((string)binary["url"]).AbsoluteUri;
            var notes = data["changelog"] as System.Collections.IList;
            if (notes == null || notes.Count > 30) throw new InvalidDataException("Notas de versión no válidas.");
            var lines = new List<string>();
            foreach (object note in notes)
            {
                if (!(note is string) || ((string)note).Length > 1000) throw new InvalidDataException("Nota de versión no válida.");
                lines.Add((string)note);
            }
            return new OtaRelease { Version = version, Url = url, Hash = hash, Size = size, Notes = string.Join("\n", lines) };
        }
        public static void VerifyBinary(string path, OtaRelease release)
        {
            if (new FileInfo(path).Length != release.Size || HashFile(path) != release.Hash) throw new InvalidDataException("El tamaño o SHA-256 del ejecutable no coincide con el paquete firmado.");
            if (AssemblyName.GetAssemblyName(path).Version.ToString(3) != release.Version) throw new InvalidDataException("El ejecutable no corresponde a la versión firmada.");
        }
        public static bool Elevated()
        {
            return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public static class OtaDownload
    {
        // Redirects are followed explicitly so HTTPS cannot silently downgrade to HTTP.
        public static Task Fetch(string url, string output, long maximum, long expected, Action<long, long> progress, CancellationToken cancel)
        {
            return Task.Run(() =>
            {
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel))
                {
                deadline.CancelAfter(120000);
                Uri uri = OtaProtocol.Https(url);
                for (int redirect = 0; redirect <= 5; redirect++)
                {
                    var request = (HttpWebRequest)WebRequest.Create(uri);
                    request.AllowAutoRedirect = false;
                    request.Timeout = 15000; request.ReadWriteTimeout = 15000;
                    request.UserAgent = "Blackwall-OTA/2";
                    request.CachePolicy = new System.Net.Cache.RequestCachePolicy(System.Net.Cache.RequestCacheLevel.NoCacheNoStore);
                    using (deadline.Token.Register(request.Abort))
                    {
                    HttpWebResponse response;
                    try { response = (HttpWebResponse)request.GetResponse(); }
                    catch (WebException ex) { if (ex.Response != null) ex.Response.Dispose(); throw; }
                    using (response)
                    {
                        int status = (int)response.StatusCode;
                        if (status == 301 || status == 302 || status == 303 || status == 307 || status == 308)
                        {
                            uri = OtaProtocol.Https(new Uri(uri, response.Headers["Location"]).AbsoluteUri);
                            continue;
                        }
                        if (status != 200 || response.ContentLength > maximum || (expected > 0 && response.ContentLength >= 0 && response.ContentLength != expected)) throw new InvalidDataException("Respuesta de descarga no válida.");
                        using (var input = response.GetResponseStream())
                        using (var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        {
                            byte[] buffer = new byte[65536]; long count = 0; int read;
                            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                deadline.Token.ThrowIfCancellationRequested(); count += read;
                                if (count > maximum || (expected > 0 && count > expected)) throw new InvalidDataException("Descarga mayor que el tamaño permitido.");
                                file.Write(buffer, 0, read);
                                if (progress != null) progress(count, expected > 0 ? expected : response.ContentLength);
                            }
                            if (expected > 0 && count != expected) throw new InvalidDataException("Descarga incompleta.");
                            file.Flush(true);
                        }
                        return;
                    }
                    }
                }
                throw new InvalidDataException("Demasiadas redirecciones.");
                }
            }, cancel);
        }
    }

    public static class OtaInstaller
    {
        public static string Root { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Blackwall", "Updates"); } }
        public static string NewStage()
        {
            string stage = Path.Combine(Root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage); return stage;
        }
        public static string CheckStage(string stage)
        {
            string full = Path.GetFullPath(stage);
            if (Path.GetDirectoryName(full) != Root || !Regex.IsMatch(Path.GetFileName(full), "^[a-f0-9]{32}$")) throw new InvalidDataException("Directorio de actualización no válido.");
            for (string directory = full; directory != null; directory = Path.GetDirectoryName(directory))
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("La ruta de actualización contiene un enlace.");
            return full;
        }
        public static string Quote(string value)
        {
            if (value.IndexOf('"') >= 0 || value.EndsWith("\\", StringComparison.Ordinal)) throw new InvalidDataException("Argumento no válido.");
            return "\"" + value + "\"";
        }
        public static async Task Start(string stage)
        {
            if (OtaProtocol.Elevated()) throw new InvalidOperationException("Para actualizar, cierra Blackwall y abre Blackwall.exe directamente sin Administrador.");
            CheckStage(stage);
            string current = Process.GetCurrentProcess().MainModule.FileName;
            string worker = Path.Combine(stage, "worker.exe");
            File.Copy(current, worker, false);
            string token = Guid.NewGuid().ToString("N");
            var info = new ProcessStartInfo(worker, "--ota-install " + Quote(stage) + " " + Process.GetCurrentProcess().Id + " " + token) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            using (var process = Process.Start(info))
            {
                for (int attempt = 0; attempt < 100; attempt++)
                {
                    if (File.Exists(Path.Combine(stage, "ready")) && File.ReadAllText(Path.Combine(stage, "ready")) == token) return;
                    if (process.HasExited) throw new IOException("El instalador no pudo prepararse. Consulta " + Path.Combine(stage, "result.txt"));
                    await Task.Delay(100);
                }
            }
            throw new TimeoutException("El instalador no respondió; Blackwall permanece abierto.");
        }
        // The parent identity supplies the only writable target; a plan cannot choose an arbitrary file.
        public static int Run(string[] args)
        {
            string stage = null;
            try
            {
                if (OtaProtocol.Elevated() || args.Length != 4 || !Regex.IsMatch(args[3], "^[a-f0-9]{32}$")) return 2;
                stage = CheckStage(args[1]);
                using (var parent = Process.GetProcessById(int.Parse(args[2], CultureInfo.InvariantCulture)))
                {
                    string target = parent.MainModule.FileName;
                    using (var session = new FileStream(target + ".ota-session.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                    {
                    string previousHash = OtaProtocol.HashFile(target);
                    if (OtaProtocol.HashFile(target) != OtaProtocol.HashFile(Process.GetCurrentProcess().MainModule.FileName)) throw new InvalidDataException("El proceso padre no coincide con el instalador.");
                    var release = OtaProtocol.Verify(File.ReadAllBytes(Path.Combine(stage, "update.json")), OtaProtocol.PublicKey());
                    if (OtaProtocol.ParseVersion(release.Version) <= OtaProtocol.ParseVersion(Assembly.GetExecutingAssembly().GetName().Version.ToString(3))) throw new InvalidDataException("Se rechazó una actualización igual o anterior.");
                    string payload = Path.Combine(stage, "Blackwall.exe");
                    OtaProtocol.VerifyBinary(payload, release);
                    File.WriteAllText(Path.Combine(stage, "ready"), args[3]);
                    if (!parent.WaitForExit(30000)) throw new TimeoutException("Blackwall no se cerró; no se sustituyó el ejecutable.");
                    if (File.Exists(Path.Combine(stage, "cancelled"))) throw new OperationCanceledException("Instalación cancelada antes de reemplazar el ejecutable.");
                    if (OtaProtocol.HashFile(target) != previousHash) throw new IOException("El ejecutable cambió durante la preparación. Vuelve a comprobar la actualización.");
                    try { Apply(payload, target, release, candidate =>
                    {
                        var start = new ProcessStartInfo(candidate, "--ota-health " + Quote(stage) + " " + args[3]) { UseShellExecute = false };
                        using (var child = Process.Start(start))
                        {
                            for (int n = 0; n < 200; n++)
                            {
                                if (child.HasExited) return false;
                                if (File.Exists(Path.Combine(stage, "healthy")) && File.ReadAllText(Path.Combine(stage, "healthy")) == args[3])
                                {
                                    Thread.Sleep(2000); return !child.HasExited;
                                }
                                Thread.Sleep(100);
                            }
                            child.CloseMainWindow();
                            // Only the candidate process started here is stopped before rollback.
                            if (!child.WaitForExit(5000)) { child.Kill(); if (!child.WaitForExit(5000)) throw new IOException("No se pudo cerrar la nueva versión; se conserva el respaldo para recuperación manual."); }
                            return false;
                        }
                    }); }
                    catch
                    {
                        if (File.Exists(target) && OtaProtocol.HashFile(target) == previousHash) Process.Start(new ProcessStartInfo(target, "--ota-rollback " + Quote(stage)) { UseShellExecute = false });
                        throw;
                    }
                    File.WriteAllText(Path.Combine(stage, "result.txt"), "OK: versión " + release.Version + " iniciada; respaldo conservado.");
                    }
                }
                return 0;
            }
            catch (Exception ex)
            {
                if (stage != null) File.WriteAllText(Path.Combine(stage, "result.txt"), "ERROR: " + ex.Message);
                return 1;
            }
        }
        public static void Apply(string payload, string target, OtaRelease release, Func<string, bool> healthy)
        {
            using (var installLock = new FileStream(target + ".ota.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
            OtaProtocol.VerifyBinary(payload, release);
            if (OtaProtocol.ParseVersion(release.Version) <= OtaProtocol.ParseVersion(AssemblyName.GetAssemblyName(target).Version.ToString(3))) throw new InvalidDataException("No se permite instalar la misma versión ni una anterior.");
            string backup = target + ".previous." + Guid.NewGuid().ToString("N");
            string pending = target + ".pending." + Guid.NewGuid().ToString("N");
            File.Copy(payload, pending, false);
            bool replaced = false;
            string journal = target + ".ota-state.json";
            Action<string> record = state => File.WriteAllText(journal, OtaProtocol.Json.Serialize(new { state = state, backup = backup, previousHash = File.Exists(backup) ? OtaProtocol.HashFile(backup) : OtaProtocol.HashFile(target), version = release.Version }));
            try
            {
                OtaProtocol.VerifyBinary(pending, release);
                record("prepared");
                File.Replace(pending, target, backup); replaced = true;
                record("installed");
                if (!healthy(target)) throw new IOException("La nueva versión no confirmó el arranque.");
                record("healthy");
            }
            catch
            {
                if (replaced) { File.Replace(backup, target, target + ".failed." + Guid.NewGuid().ToString("N")); record("rolled-back"); }
                throw;
            }
            finally { if (File.Exists(pending)) File.Delete(pending); }
            }
        }
        public static void ReportHealthy(string[] args)
        {
            if (args.Length == 3 && args[0] == "--ota-health" && !OtaProtocol.Elevated() && Regex.IsMatch(args[2], "^[a-f0-9]{32}$"))
                File.WriteAllText(Path.Combine(CheckStage(args[1]), "healthy"), args[2]);
        }
    }
}
