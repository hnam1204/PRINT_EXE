using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

if (args.Length != 2) return 2;
string manifestPath = Path.GetFullPath(args[0]);
string keyPath = Path.GetFullPath(args[1]);
var root = JsonNode.Parse(File.ReadAllText(manifestPath))?.AsObject() ?? throw new InvalidDataException("Manifest JSON không hợp lệ.");
string Get(string name, string fallback = "") => root[name]?.GetValue<string>() ?? fallback;
bool critical = root["isCritical"]?.GetValue<bool>() ?? false;
var changelog = root["changelog"]?.AsArray().Select(item => item?.GetValue<string>() ?? string.Empty) ?? Enumerable.Empty<string>();
string canonical = string.Join("\n", Get("version"), Get("channel"), DateTime.Parse(Get("releaseDate")).ToUniversalTime().ToString("O"), Get("downloadUrl"), Get("sha256"), Get("minSupportedVersion"), critical ? "true" : "false", string.Join("\n", changelog), Get("releaseNotes"));
using RSA rsa = RSA.Create();
rsa.ImportFromPem(File.ReadAllText(keyPath));
root["signatureBase64"] = Convert.ToBase64String(rsa.SignData(Encoding.UTF8.GetBytes(canonical), HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
File.WriteAllText(manifestPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
return 0;
