using FluentAssertions;
using ErpGenerator.Runtime.Shared.Bundles;

namespace ErpGenerator.Runtime.Tests;

public sealed class BundleAcceptanceActorPrincipalIdTests
{
    [Fact]
    public void Derive_is_deterministic_and_frozen_for_the_exact_v3_identity_tuple()
    {
        var suiteHash = new string('a', 64);

        var first = BundleAcceptanceActorPrincipalId.Derive(
            suiteHash,
            "actor_credit_test",
            "owner_actor");
        var second = BundleAcceptanceActorPrincipalId.Derive(
            suiteHash,
            "actor_credit_test",
            "owner_actor");

        first.Should().Be(-1_531_719_631L);
        second.Should().Be(first);
    }

    [Fact]
    public void Derive_is_domain_bound_and_always_uses_the_negative_int32_range()
    {
        var suiteHash = new string('a', 64);
        var principals = new[]
        {
            BundleAcceptanceActorPrincipalId.Derive(suiteHash, "test_one", "actor_one"),
            BundleAcceptanceActorPrincipalId.Derive(suiteHash, "test_two", "actor_one"),
            BundleAcceptanceActorPrincipalId.Derive(suiteHash, "test_one", "actor_two"),
            BundleAcceptanceActorPrincipalId.Derive(new string('b', 64), "test_one", "actor_one")
        };

        principals.Should().OnlyHaveUniqueItems();
        principals.Should().OnlyContain(value =>
            value >= -(long)int.MaxValue && value <= -1L);
    }

    [Fact]
    public void Derive_rejects_noncanonical_suite_hashes()
    {
        var invalid = new[]
        {
            string.Empty,
            " ",
            new string('a', 63),
            new string('A', 64),
            new string('g', 64)
        };

        foreach (var suiteHash in invalid)
        {
            var derive = () => BundleAcceptanceActorPrincipalId.Derive(
                suiteHash,
                "test_one",
                "actor_one");

            derive.Should().Throw<ArgumentException>();
        }
    }

    [Theory]
    [InlineData("", "actor_one")]
    [InlineData(" ", "actor_one")]
    [InlineData(" test_one", "actor_one")]
    [InlineData("test_one ", "actor_one")]
    [InlineData("test_one", "")]
    [InlineData("test_one", " ")]
    [InlineData("test_one", " actor_one")]
    [InlineData("test_one", "actor_one ")]
    public void Derive_rejects_nonexact_test_and_actor_components(
        string testKey,
        string actorId)
    {
        var derive = () => BundleAcceptanceActorPrincipalId.Derive(
            new string('a', 64),
            testKey,
            actorId);

        derive.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Derive_rejects_null_arguments()
    {
        Action nullSuite = () => BundleAcceptanceActorPrincipalId.Derive(null!, "test_one", "actor_one");
        Action nullTest = () => BundleAcceptanceActorPrincipalId.Derive(new string('a', 64), null!, "actor_one");
        Action nullActor = () => BundleAcceptanceActorPrincipalId.Derive(new string('a', 64), "test_one", null!);

        nullSuite.Should().Throw<ArgumentNullException>();
        nullTest.Should().Throw<ArgumentNullException>();
        nullActor.Should().Throw<ArgumentNullException>();
    }
}
