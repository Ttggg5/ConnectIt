using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace ConnectIt.Wpf.Services;

/// <summary>
/// 這台裝置的身分:一把固定的自簽 ECDSA P-256 憑證,配對連線一律走雙向 TLS(見 <see cref="ConnectionService"/>),
/// 兩邊各自出示自己的憑證。憑證不是由 CA 簽發的,不靠憑證鏈驗證身分,而是:
/// <list type="bullet">
/// <item>第一次配對時,兩邊畫面都顯示同一組 6 位數配對碼(<see cref="PairingCode"/>,由雙方憑證指紋算出),
/// 使用者核對一致才接受——中間人沒辦法讓兩邊看到同一組數字。</item>
/// <item>勾選「信任此裝置」時記住對方的憑證指紋(<see cref="Fingerprint"/>),之後只認指紋、不認名稱。</item>
/// </list>
/// 憑證(含私鑰)存成 PFX,再用 DPAPI 以目前 Windows 使用者的身分加密,放在 %LocalAppData%\ConnectIt\identity.bin;
/// 跟設定檔不同,不放在程式資料夾裡,自我更新覆蓋程式檔案也不會動到它。
/// 指紋與配對碼的算法必須跟 Android 端 DeviceIdentity.kt 完全一致。
/// </summary>
public sealed class DeviceIdentity
{
    private DeviceIdentity(X509Certificate2 certificate)
    {
        Certificate = certificate;
        Fingerprint = FingerprintOf(certificate);
    }

    /// <summary>含私鑰的憑證,TLS 握手時出示給對方。</summary>
    public X509Certificate2 Certificate { get; }

    /// <summary>憑證 DER 的 SHA-256(小寫十六進位)。</summary>
    public string Fingerprint { get; }

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ConnectIt", "identity.bin");

    /// <summary>讀取已存的身分;沒有(或讀不出來)就產生一把新的並存起來。</summary>
    public static DeviceIdentity LoadOrCreate(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var pfx = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
                return new DeviceIdentity(LoadPfx(pfx));
            }
        }
        catch
        {
            // 檔案損毀或換了 Windows 使用者(DPAPI 解不開):產生新的身分,之前信任過這台的裝置需要重新配對。
        }

        var (certificate, pfxBytes) = Generate();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, ProtectedData.Protect(pfxBytes, null, DataProtectionScope.CurrentUser));
        }
        catch
        {
            // 存不起來就只用這一次,下次啟動會換一把新的(對方需要重新配對)。
        }

        return new DeviceIdentity(certificate);
    }

    /// <summary>只存在記憶體裡的身分(測試用)。</summary>
    public static DeviceIdentity CreateEphemeral() => new(Generate().Certificate);

    private static (X509Certificate2 Certificate, byte[] Pfx) Generate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=ConnectIt device", key, HashAlgorithmName.SHA256);
        var now = DateTimeOffset.UtcNow;
        using var selfSigned = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(30));
        var pfx = selfSigned.Export(X509ContentType.Pkcs12);
        // 直接用 CreateSelfSigned 回傳的憑證時私鑰是「暫時性」的,Windows 的 SChannel(SslStream)用不了,
        // 匯出成 PFX 再匯入就會變成一般的金鑰容器。
        return (LoadPfx(pfx), pfx);
    }

    private static X509Certificate2 LoadPfx(byte[] pfx) =>
        X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet);

    public static string FingerprintOf(X509Certificate certificate) =>
        Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData())).ToLowerInvariant();

    /// <summary>
    /// 兩台裝置的 6 位數配對碼:兩個指紋依字典序排好、用 ":" 串起來取 SHA-256,
    /// 前 4 bytes 當成 big-endian 無號整數除以 1,000,000 的餘數,補零到 6 位。兩邊算出來一定一樣。
    /// </summary>
    public static string PairingCode(string fingerprintA, string fingerprintB)
    {
        var (first, second) = string.CompareOrdinal(fingerprintA, fingerprintB) <= 0
            ? (fingerprintA, fingerprintB)
            : (fingerprintB, fingerprintA);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{first}:{second}"));
        var value = BinaryPrimitives.ReadUInt32BigEndian(hash);
        return (value % 1_000_000).ToString("D6");
    }

    /// <summary>顯示用:「123 456」。</summary>
    public static string FormatPairingCode(string code) => code.Length == 6 ? $"{code[..3]} {code[3..]}" : code;
}
