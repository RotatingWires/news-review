using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NewsReview.Tests;

/// <summary>Changes the process's environment and SignInKeys' shared secret, so it runs apart from tests that start the app.</summary>
[CollectionDefinition("Environment", DisableParallelization = true)]
public class EnvironmentCollection;

/// <summary>src/SignInKeys.cs: on Linux the sign-in keys are encrypted with the systemd credential when it is there.</summary>
[Collection("Environment")]
public class SignInKeysTests : IDisposable
{
    readonly TempDirectory directory = new();
    string Keys => directory.File("keys");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CREDENTIALS_DIRECTORY", null);
        directory.Dispose();
    }

    /// <summary>A fresh service provider, as each start of the app builds one.</summary>
    IDataProtector Start()
    {
        var services = new ServiceCollection().AddLogging();
        SignInKeys.Configure(services, "NewsReview", _ => Keys, "newsreview");
        return services.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>().CreateProtector("test");
    }

    [Fact]
    public void KeysAreEncryptedWithTheCredentialAndReadAfterARestart()
    {
        if (OperatingSystem.IsWindows()) return; // Windows uses machine-scope DPAPI instead.
        var credentials = Directory.CreateDirectory(directory.File("credentials")).FullName;
        File.WriteAllBytes(Path.Combine(credentials, "newsreview-sign-in-keys"), System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

        // A key written before the credential existed is plain, readable only by its owner in an owner-only folder.
        var protectedText = Start().Protect("signed in");
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Keys));
        Assert.Contains("masterKey", File.ReadAllText(Assert.Single(Directory.GetFiles(Keys, "key-*.xml"))));

        // With the credential, that key is encrypted in place, and what it protected still reads.
        Environment.SetEnvironmentVariable("CREDENTIALS_DIRECTORY", credentials);
        Assert.Equal("signed in", Start().Unprotect(protectedText));
        var key = File.ReadAllText(Assert.Single(Directory.GetFiles(Keys, "key-*.xml")));
        Assert.Contains("<encryptedKey", key);
        Assert.DoesNotContain("masterKey", key);
        Assert.Contains("encrypted with the newsreview-sign-in-keys systemd credential", SignInKeys.Describe(Keys, "newsreview"));

        // Without it, the encrypted key cannot be read, so nothing it protected is accepted.
        Environment.SetEnvironmentVariable("CREDENTIALS_DIRECTORY", null);
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => Start().Unprotect(protectedText));
    }
}
