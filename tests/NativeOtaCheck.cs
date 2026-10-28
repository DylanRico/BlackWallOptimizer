using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Blackwall;

internal static class NativeOtaCheck
{
    private static int checks;
    private static void Check(bool value, string label) { if (!value) throw new Exception(label); checks++; }
    private static void Reject(Action action, string label)
    {
        bool failed = false; try { action(); } catch { failed = true; } Check(failed, label);
    }
    private static void RejectWith(Action action, Func<Exception, bool> expected, string label)
    {
        Exception failure = null; try { action(); } catch (Exception ex) { failure = ex; }
        Check(failure != null && expected(failure), label + (failure == null ? " (unexpected success)" : " (" + failure.GetType().Name + ")"));
    }
    private static byte[] Sign(RSACryptoServiceProvider rsa, Dictionary<string, object> payload)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(OtaProtocol.Json.Serialize(payload));
        return Encoding.UTF8.GetBytes(OtaProtocol.Json.Serialize(new { payload = Convert.ToBase64String(bytes), signature = Convert.ToBase64String(rsa.SignData(bytes, "SHA256")) }));
    }
    private static Dictionary<string, object> Payload(string path, string version)
    {
        return new Dictionary<string, object> { { "schemaVersion", 1 }, { "appVersion", version }, { "binary", new Dictionary<string, object> { { "url", "https://example.invalid/Blackwall.exe" }, { "sha256", OtaProtocol.HashFile(path) }, { "size", new FileInfo(path).Length } } }, { "changelog", new[] { "Prueba local de contrato OTA" } } };
    }
    private static void Wait(Func<bool> condition, string label)
    {
        var watch = Stopwatch.StartNew(); while (!condition()) { if (watch.ElapsedMilliseconds > 35000) throw new Exception(label); Thread.Sleep(50); }
    }
    private static void CompileHost(string project, string folder, string version, bool fail)
    {
        Directory.CreateDirectory(folder);
        string source = Path.Combine(folder, "Host.cs");
        File.WriteAllText(source, "using System; using System.IO; using System.Threading; using Blackwall; [assembly:System.Reflection.AssemblyVersion(\"" + version + ".0\")] class Host { static void Main(string[] args) { if(args.Length>0 && args[0]==\"--ota-install\") { Environment.ExitCode=OtaInstaller.Run(args); return; } if(args.Length>0 && args[0]==\"--ota-prepared\") { Environment.ExitCode=OtaInstaller.RunPrepared(args); return; } if(args.Length==2 && args[0]==\"--wait\") { for(int i=0;i<600 && !File.Exists(Path.Combine(args[1],\"finish\"));i++) Thread.Sleep(50); return; } if(args.Length==2 && args[0]==\"--parent\") { try { OtaInstaller.Start(args[1]).GetAwaiter().GetResult(); } catch { Environment.ExitCode=3; } return; } if(args.Length>0 && args[0]==\"--ota-health\") { " + (fail ? "Environment.ExitCode=4; return;" : "OtaInstaller.ReportHealthy(args); Thread.Sleep(4000);") + " } } }");
        string compiler = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"Microsoft.NET\Framework64\v4.0.30319\csc.exe");
        string arguments = "/nologo /target:exe /out:" + OtaInstaller.Quote(Path.Combine(folder, "Host.exe")) + " /r:System.Web.Extensions.dll /resource:" + OtaInstaller.Quote(Path.Combine(project, "test-public.xml")) + ",update-public-key.xml" + " /resource:" + OtaInstaller.Quote(Path.Combine(project, "padding.bin")) + ",padding" + " " + OtaInstaller.Quote(source) + " " + OtaInstaller.Quote(Path.Combine(project, "OtaUpdate.cs"));
        using (var process = Process.Start(new ProcessStartInfo(compiler, arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }))
        {
            string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd(); process.WaitForExit();
            Check(process.ExitCode == 0, "Compile isolated host: " + output);
        }
    }
    private static void ProcessUpdate(string fixture, string candidate, RSACryptoServiceProvider rsa, bool healthy)
    {
        string targetFolder = Path.Combine(fixture, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(targetFolder);
        string target = Path.Combine(targetFolder, "Host.exe"); File.Copy(Path.Combine(fixture, "old", "Host.exe"), target);
        string previousHash = OtaProtocol.HashFile(target);
        string stage = OtaInstaller.NewStage();
        File.Copy(candidate, Path.Combine(stage, "Blackwall.exe"));
        File.WriteAllBytes(Path.Combine(stage, "update.json"), Sign(rsa, Payload(candidate, "1.3.1")));
        using (var parent = Process.Start(new ProcessStartInfo(target, "--parent " + OtaInstaller.Quote(stage)) { UseShellExecute = false, CreateNoWindow = true }))
        {
            Check(parent.WaitForExit(15000) && parent.ExitCode == 0, "Parent hands off after the worker readiness handshake");
        }
        Wait(() => File.Exists(Path.Combine(stage, "result.txt")), "Updater result timeout");
        string result = File.ReadAllText(Path.Combine(stage, "result.txt"));
        Check(result.StartsWith(healthy ? "OK:" : "ERROR:"), "Worker reports the real outcome: " + result);
        Check(OtaProtocol.HashFile(target) == (healthy ? OtaProtocol.HashFile(candidate) : previousHash), healthy ? "Signed test app replaces the copy and confirms health" : "Failed startup restores the previous test app");
        Check(Directory.GetFiles(targetFolder, healthy ? "*.previous.*" : "*.failed.*").Length == 1, "Recovery artifact is retained");
    }
    private static void RejectedProcessUpdate(string fixture, string candidate, RSACryptoServiceProvider rsa, string version, bool cancel)
    {
        string folder = Path.Combine(fixture, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        string target = Path.Combine(folder, "Host.exe"); File.Copy(Path.Combine(fixture, "old", "Host.exe"), target);
        string original = OtaProtocol.HashFile(target), stage = OtaInstaller.NewStage();
        File.Copy(candidate, Path.Combine(stage, "Blackwall.exe"));
        File.WriteAllBytes(Path.Combine(stage, "update.json"), Sign(rsa, Payload(candidate, version)));
        if (cancel) File.WriteAllText(Path.Combine(stage, "cancelled"), "1");
        using (var parent = Process.Start(new ProcessStartInfo(target, "--parent " + OtaInstaller.Quote(stage)) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true }))
        {
            Check(parent.WaitForExit(15000), "Rejected handoff is bounded");
        }
        Wait(() => File.Exists(Path.Combine(stage, "result.txt")), "Rejected update result timeout");
        Check(File.ReadAllText(Path.Combine(stage, "result.txt")).StartsWith("ERROR:") && OtaProtocol.HashFile(target) == original, cancel ? "Cancelled handoff never mutates the test installation" : "Worker rejects same-version replay before replacement");
    }
    private static void PreparedProcessUpdate(string fixture, string candidate, RSACryptoServiceProvider rsa, bool invalid, bool cancel)
    {
        string folder = Path.Combine(fixture, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        string target = Path.Combine(folder, "Host.exe"); File.Copy(Path.Combine(fixture, "old", "Host.exe"), target);
        string original = OtaProtocol.HashFile(target), stage = OtaInstaller.NewStage();
        File.Copy(candidate, Path.Combine(stage, "Blackwall.exe"));
        File.WriteAllBytes(Path.Combine(stage, "update.json"), Sign(rsa, Payload(candidate, "1.3.1")));
        string token = Guid.NewGuid().ToString("N");
        using (var parent = Process.Start(new ProcessStartInfo(target, "--wait " + OtaInstaller.Quote(stage)) { UseShellExecute = false, CreateNoWindow = true }))
        using (var helper = Process.Start(new ProcessStartInfo(target, "--ota-prepared " + OtaInstaller.Quote(stage) + " 1.3.1 " + (invalid ? new string('a', 64) : OtaProtocol.HashFile(candidate)) + " " + parent.Id + " " + token) { UseShellExecute = false, CreateNoWindow = true }))
        {
            if (invalid)
            {
                Check(helper.WaitForExit(15000) && helper.ExitCode != 0 && !File.Exists(Path.Combine(stage, "prepared-ready")), "unmatched prepared hash is rejected before closing the original");
                Check(!parent.HasExited && OtaProtocol.HashFile(target) == original, "rejected prepared handoff leaves the original running and unchanged");
                File.WriteAllText(Path.Combine(stage, "finish"), "1");
                Check(parent.WaitForExit(5000), "test parent exits on its own signal");
                return;
            }
            Wait(() => File.Exists(Path.Combine(stage, "prepared-ready")) || helper.HasExited, "Prepared helper readiness timeout");
            Check(!helper.HasExited && File.ReadAllText(Path.Combine(stage, "prepared-ready")) == token, "prepared helper verifies package before reporting readiness");
            Check(!parent.HasExited && OtaProtocol.HashFile(target) == original, "original stays running and unchanged while handoff is prepared");
            if (cancel) File.WriteAllText(Path.Combine(stage, "cancelled"), "1");
            File.WriteAllText(Path.Combine(stage, "finish"), "1");
            Check(parent.WaitForExit(5000) && helper.WaitForExit(15000), "prepared helper waits for original to close");
            if (cancel)
            {
                Check(helper.ExitCode != 0 && OtaProtocol.HashFile(target) == original && !File.Exists(Path.Combine(stage, "result.txt")), "cancellation before replacement preserves the original executable");
                return;
            }
            Check(helper.ExitCode == 0, "verified helper launches installer after original exits");
        }
        Wait(() => File.Exists(Path.Combine(stage, "result.txt")), "Prepared worker result timeout");
        Check(File.ReadAllText(Path.Combine(stage, "result.txt")).StartsWith("OK:") && OtaProtocol.HashFile(target) == OtaProtocol.HashFile(candidate), "prepared update installs verified candidate after the original exits");
    }
    private static void Network(string baseUrl, string thumbprint, string folder)
    {
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        Directory.CreateDirectory(folder);
        Func<string, long, long, string> fetch = (route, maximum, expected) =>
        {
            string path = Path.Combine(folder, Guid.NewGuid().ToString("N"));
            OtaDownload.Fetch(baseUrl + route, path, maximum, expected, null, CancellationToken.None).GetAwaiter().GetResult(); return path;
        };
        RejectWith(() => fetch("manifest", 262144, 0), ex => ex is WebException && (((WebException)ex).Status == WebExceptionStatus.TrustFailure || ((WebException)ex).Status == WebExceptionStatus.SecureChannelFailure), "Production transport rejects the untrusted TLS certificate");
        // Test process only: trust this ephemeral local certificate, never all certificates.
        ServicePointManager.ServerCertificateValidationCallback = (sender, cert, chain, errors) => { if (cert == null || ((HttpWebRequest)sender).RequestUri.Host != "127.0.0.1") return false; using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(cert.GetRawCertData())).Replace("-", "") == thumbprint; };
        var release = OtaProtocol.Verify(File.ReadAllBytes(fetch("manifest", 262144, 0)), OtaProtocol.PublicKey());
        string binary = Path.Combine(folder, "verified-binary.exe");
        long transferred = 0;
        OtaDownload.Fetch(release.Url, binary, 104857600, release.Size, (count, total) => transferred = count, CancellationToken.None).GetAwaiter().GetResult();
        OtaProtocol.VerifyBinary(binary, release); Check(true, "Real HTTPS transport downloads and verifies a Postman-served signed package");
        Check(transferred == release.Size, "Download progress reflects actual bytes");
        RejectWith(() => OtaProtocol.Verify(File.ReadAllBytes(fetch("tampered", 262144, 0)), OtaProtocol.PublicKey()), ex => ex is InvalidDataException, "Tampered remote envelope is rejected");
        RejectWith(() => fetch("oversize", 32, 0), ex => ex is InvalidDataException, "Response size is bounded");
        Reject(() => fetch("truncated", 104857600, release.Size), "Interrupted download cannot be accepted");
        RejectWith(() => fetch("failure", 262144, 0), ex => ex is WebException && ((WebException)ex).Status == WebExceptionStatus.ProtocolError, "HTTP 500 is propagated as failure");
        RejectWith(() => fetch("downgrade", 262144, 0), ex => ex is InvalidDataException && ex.Message.Contains("HTTPS"), "HTTPS-to-HTTP redirects are rejected");
        RejectWith(() => fetch("loop", 262144, 0), ex => ex is InvalidDataException && ex.Message.Contains("redirecciones"), "Redirect loops are bounded");
        var timeout = Stopwatch.StartNew();
        RejectWith(() => fetch("slow", 262144, 0), ex => ex is WebException && ((WebException)ex).Status == WebExceptionStatus.Timeout, "Unresponsive server times out");
        Check(timeout.ElapsedMilliseconds >= 14000 && timeout.ElapsedMilliseconds < 24000, "Server timeout is bounded near the configured 15 seconds");
        ServicePointManager.ServerCertificateValidationCallback = null;
    }
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 4 && args[0] == "--network-fixture")
            {
                Directory.CreateDirectory(args[3]);
                using (var key = new RSACryptoServiceProvider(3072))
                {
                    key.PersistKeyInCsp = false;
                    string binary = Path.Combine(Path.GetFullPath(args[1]), "Blackwall.exe");
                    string version = System.Reflection.AssemblyName.GetAssemblyName(binary).Version.ToString(3);
                    var data = Payload(binary, version); ((Dictionary<string, object>)data["binary"])["url"] = args[2] + "binary";
                    File.WriteAllBytes(Path.Combine(args[3], "update.json"), Sign(key, data));
                    File.WriteAllText(Path.Combine(args[3], "test-public.xml"), key.ToXmlString(false));
                    File.Copy(binary, Path.Combine(args[3], "Blackwall.exe"));
                }
                return 0;
            }
            if (args.Length == 4 && args[0] == "--network") { Network(args[1], args[2], args[3]); Console.WriteLine("PASS: " + checks + " OTA HTTPS/Postman checks."); return 0; }
            string package = Path.GetFullPath(args[0]), project = Path.GetFullPath(args[1]), fixture = Path.GetFullPath(args[2]); Directory.CreateDirectory(fixture);
            byte[] envelope = File.ReadAllBytes(Path.Combine(package, "update.json"));
            var release = OtaProtocol.Verify(envelope, OtaProtocol.PublicKey());
            string payload = Path.Combine(package, "Blackwall.exe");
            OtaProtocol.VerifyBinary(payload, release); Check(true, "Prepared package signature, PE version, size and SHA256 agree");
            foreach (string bad in new[] { "", "1.2", "01.2.3", "1.2.3.4", "65535.0.0", "v1.2.3", "1.2.3-beta" }) Reject(() => OtaProtocol.ParseVersion(bad), "Reject malformed version " + bad);
            foreach (string bad in new[] { "http://example.invalid", "https://user:password@example.invalid", "https://example.invalid/#fragment", "file:///C:/test" }) Reject(() => OtaProtocol.Https(bad), "Reject unsafe URL");
            Reject(() => OtaProtocol.Verify(Encoding.UTF8.GetBytes("{\"appVersion\":\"999.0.0\"}"), OtaProtocol.PublicKey()), "Unsigned legacy manifest is rejected");
            Reject(() => OtaProtocol.Verify(new byte[262145], OtaProtocol.PublicKey()), "Oversize manifest rejected before parsing");
            string cleanupStage = OtaInstaller.NewStage();
            File.WriteAllText(Path.Combine(cleanupStage, "update.json"), "fixture");
            File.WriteAllText(Path.Combine(cleanupStage, "unknown-user-file.txt"), "keep");
            OtaInstaller.CleanStage(cleanupStage);
            Check(!File.Exists(Path.Combine(cleanupStage, "update.json")) && File.Exists(Path.Combine(cleanupStage, "unknown-user-file.txt")), "cleanup deletes only known stage files and preserves unknown content");
            File.Delete(Path.Combine(cleanupStage, "unknown-user-file.txt")); Directory.Delete(cleanupStage);
            using (var rsa = new RSACryptoServiceProvider(3072))
            {
                rsa.PersistKeyInCsp = false;
                var data = Payload(payload, release.Version);
                byte[] signed = Sign(rsa, data);
                Reject(() => OtaProtocol.Verify(signed, OtaProtocol.PublicKey()), "Untrusted signing key is rejected");
                var own = OtaProtocol.Verify(signed, rsa.ToXmlString(false)); Check(own.Hash == release.Hash, "Signed contract is accepted with its pinned key");
                var binary = (Dictionary<string, object>)data["binary"];
                binary["sha256"] = ""; Reject(() => OtaProtocol.Verify(Sign(rsa, data), rsa.ToXmlString(false)), "Hash cannot be omitted"); binary["sha256"] = release.Hash;
                data["appVersion"] = "1.3"; Reject(() => OtaProtocol.Verify(Sign(rsa, data), rsa.ToXmlString(false)), "Signed malformed version is still rejected"); data["appVersion"] = release.Version;
                data["unknown"] = true; Reject(() => OtaProtocol.Verify(Sign(rsa, data), rsa.ToXmlString(false)), "Unknown schema fields rejected"); data.Remove("unknown");
                string tampered = Path.Combine(fixture, "tampered.exe"); File.Copy(payload, tampered); using (var file = File.Open(tampered, FileMode.Open, FileAccess.ReadWrite)) { file.Position = 500; int value = file.ReadByte(); file.Position = 500; file.WriteByte((byte)(value ^ 255)); }
                Reject(() => OtaProtocol.VerifyBinary(tampered, release), "Corrupt executable is rejected");
                string target = Path.Combine(fixture, "target.exe"); File.Copy(Path.Combine(project, "Blackwall.exe"), target); string original = OtaProtocol.HashFile(target);
                Reject(() => OtaInstaller.Apply(payload, target, release, candidate => false), "Failed health check triggers rollback");
                Check(OtaProtocol.HashFile(target) == original, "Rollback preserves the exact previous executable");
                using (var locked = File.Open(target + ".ota.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) Reject(() => OtaInstaller.Apply(payload, target, release, candidate => true), "Concurrent installation is rejected");
                Check(OtaProtocol.HashFile(target) == original, "Concurrency failure never changes the target");
                using (var locked = File.Open(target, FileMode.Open, FileAccess.Read, FileShare.Read)) Reject(() => OtaInstaller.Apply(payload, target, release, candidate => true), "Locked installation is not overwritten");
                Check(OtaProtocol.HashFile(target) == original, "Locked-target failure preserves the original bytes");
                OtaInstaller.Apply(payload, target, release, candidate => true); Check(OtaProtocol.HashFile(target) == release.Hash, "Successful transaction installs the verified bytes");
                Reject(() => OtaInstaller.Apply(payload, target, release, candidate => true), "Same-version reinstallation is rejected");
                Check(Directory.GetFiles(fixture, "target.exe.previous.*").Length == 1, "Successful transaction retains previous backup");
                string hostFixture = Path.Combine(fixture, "hosts"); Directory.CreateDirectory(hostFixture);
                File.WriteAllText(Path.Combine(hostFixture, "test-public.xml"), rsa.ToXmlString(false)); File.WriteAllBytes(Path.Combine(hostFixture, "padding.bin"), new byte[65536]); File.Copy(Path.Combine(project, "native-wpf", "OtaUpdate.cs"), Path.Combine(hostFixture, "OtaUpdate.cs"));
                CompileHost(hostFixture, Path.Combine(hostFixture, "old"), "1.3.0", false);
                CompileHost(hostFixture, Path.Combine(hostFixture, "good"), "1.3.1", false);
                CompileHost(hostFixture, Path.Combine(hostFixture, "bad"), "1.3.1", true);
                ProcessUpdate(hostFixture, Path.Combine(hostFixture, "good", "Host.exe"), rsa, true);
                ProcessUpdate(hostFixture, Path.Combine(hostFixture, "bad", "Host.exe"), rsa, false);
                PreparedProcessUpdate(hostFixture, Path.Combine(hostFixture, "good", "Host.exe"), rsa, false, false);
                PreparedProcessUpdate(hostFixture, Path.Combine(hostFixture, "good", "Host.exe"), rsa, false, true);
                PreparedProcessUpdate(hostFixture, Path.Combine(hostFixture, "good", "Host.exe"), rsa, true, false);
                RejectedProcessUpdate(hostFixture, Path.Combine(hostFixture, "old", "Host.exe"), rsa, "1.3.0", false);
                RejectedProcessUpdate(hostFixture, Path.Combine(hostFixture, "good", "Host.exe"), rsa, "1.3.1", true);
            }
            Console.WriteLine("PASS: " + checks + " OTA protocol, transaction and real subprocess checks (disposable copies only)."); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("FAIL: " + ex); return 1; }
    }
}
