namespace RynthCore.App.Avalonia;

/// <summary>
/// Public halves of the update-signing keys: the primary one that signs releases and the
/// offline backup that replaces it if it is ever lost. SubjectPublicKeyInfo, base64.
///
/// Written by installer\New-UpdateSigningKey.ps1 — run it, then rebuild. Empty means updates
/// are switched off: with no trusted key no feed can verify, so nothing is ever installed.
/// </summary>
internal static class UpdateTrust
{
    public static readonly string[] PublicKeys =
    {
        // BEGIN KEYS (New-UpdateSigningKey.ps1)
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEYVKNob7tzli73BPSjVgJjWWYUCoJjA6wwHUy6c+mMfy32V3I5cuheI+jMSMFk54ywcRgNBypfUqibrtKFcG7qQ==", // primary f0bc55ee71c184dd
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAESJ7xQmQgVy90BJE93YSGOpZN11nB9jSbBAVbji8weZbhKlI/5aKnJfTMAWHHsl08HmCAPLXU4nfriuODegc8vw==", // backup 2c2ed4a9042ac855
        // END KEYS
    };
}
