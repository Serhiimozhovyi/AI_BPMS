using System.Security.Cryptography;
using OpenBP.Data.Credentials;

namespace OpenBP.Data.Tests.Credentials;

public class CredentialEncryptionKeyTests
{
    [Fact]
    public void FromBase64_Accepts32Bytes()
    {
        var key = CredentialEncryptionKey.FromBase64(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));

        Assert.NotNull(key);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("не base64!")]
    public void FromBase64_RejectsMissingOrInvalid(string? value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => CredentialEncryptionKey.FromBase64(value));
        Assert.Contains(CredentialEncryptionKey.EnvironmentVariable, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(31)]
    [InlineData(33)]
    [InlineData(64)]
    public void FromBase64_RejectsWrongLength(int length)
    {
        var value = Convert.ToBase64String(RandomNumberGenerator.GetBytes(length));

        var ex = Assert.Throws<InvalidOperationException>(() => CredentialEncryptionKey.FromBase64(value));
        Assert.DoesNotContain(value, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FromEnvironment_WithoutVariable_FailsStartup()
    {
        var previous = Environment.GetEnvironmentVariable(CredentialEncryptionKey.EnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(CredentialEncryptionKey.EnvironmentVariable, null);
            Assert.Throws<InvalidOperationException>(CredentialEncryptionKey.FromEnvironment);

            Environment.SetEnvironmentVariable(
                CredentialEncryptionKey.EnvironmentVariable,
                Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            Assert.NotNull(CredentialEncryptionKey.FromEnvironment());
        }
        finally
        {
            Environment.SetEnvironmentVariable(CredentialEncryptionKey.EnvironmentVariable, previous);
        }
    }
}
