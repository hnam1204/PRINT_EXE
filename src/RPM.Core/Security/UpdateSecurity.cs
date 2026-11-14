using System.Security.Cryptography;
using System.Text;
using RPM.Core.Update;

namespace RPM.Core.Security;

public static class UpdateSecurity
{
    private const string PublicKeyPem = """
-----BEGIN PUBLIC KEY-----
MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEA4xxuwtfD43Guqbcbu2Dw
e8kC9Jo7fHedKWp7TLeCwAPGILzZqhiK1Nhf/zABVIIFB2KC+IDKH1vkzeJFaM1m
EOqpI6KKHKnYOhYSXnRLYPJ7k7fPXpsrRPAjeFe6O23ueo1SrTW48inOYnSb+VPP
KOyGZ70AjmIpqjm6GmD/3lHuNwNQvoECkmZdeNVJDUakybDe7H05qZjk556YPfzl
TcJ2pPXX0RCAYwPTcJ9BXjh0FIsauqwVYZeZzt7n0H1GnLCXQNka6IQdjgv3uelg
/9Z1ir3OUEh17L6ryovXSCk7sJrXeZX/g5S/82jEi6RCwfQQ1Fso9B8Qes02yNNA
/D2HSNA9r8fAney8QB/Qri0nCeB5Rx6Ctqv5aeQ9fL1Ci758mYcpES6wg2BLyi7h
kRulHRW4SS17vmyCnoTRRYphMyuciyppRiX2uH0GHyuzKnQuB8+MPBtawsd4ev6f
FgqLOb9g+OtLE/YVXu7SDkCN9OdLWMtAVpJsety/lpWZAgMBAAE=
-----END PUBLIC KEY-----
""";

    public static bool VerifyManifest(UpdateManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.SignatureBase64) ||
            string.IsNullOrWhiteSpace(manifest.Sha256) ||
            string.IsNullOrWhiteSpace(manifest.DownloadUrl)) return false;

        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(PublicKeyPem);
            return rsa.VerifyData(Encoding.UTF8.GetBytes(Canonicalize(manifest)),
                Convert.FromBase64String(manifest.SignatureBase64), HashAlgorithmName.SHA256,
                RSASignaturePadding.Pss);
        }
        catch (FormatException) { return false; }
        catch (CryptographicException) { return false; }
    }

    public static string Canonicalize(UpdateManifest manifest) => string.Join("\n",
        manifest.Version, manifest.Channel, manifest.ReleaseDate.ToUniversalTime().ToString("O"),
        manifest.DownloadUrl, manifest.Sha256, manifest.MinSupportedVersion,
        manifest.IsCritical ? "true" : "false", string.Join("\n", manifest.Changelog), manifest.ReleaseNotes);
}
