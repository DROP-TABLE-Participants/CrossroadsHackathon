using FluentAssertions;
using ErpGenerator.Runtime.Data.Databases;

namespace ErpGenerator.Runtime.Tests;

public sealed class PasswordHasherTests
{
    [Fact]
    public void Runtime_password_hashes_are_verifiable_without_reusing_landing_identity()
    {
        var hash = PasswordHasher.Hash("correct-password");

        PasswordHasher.Verify("correct-password", hash).Should().BeTrue();
        PasswordHasher.Verify("wrong-password", hash).Should().BeFalse();
    }
}
