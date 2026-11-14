using Microsoft.Extensions.Time.Testing;
using Trainer.Domain.Ids;
using Trainer.Domain.Questions;
using Trainer.Domain.Users;

namespace Trainer.Domain.Tests;

public sealed class UlidTextTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_KnownVector()
    {
        // Time part from the ULID specification.
        string ulid = UlidText.Create(DateTimeOffset.FromUnixTimeMilliseconds(1469922850259), new byte[10]);
        Assert.Equal("01ARZ3NDEK0000000000000000", ulid);
    }

    [Fact]
    public void Create_AllOnesRandomness() =>
        Assert.EndsWith("ZZZZZZZZZZZZZZZZ", UlidText.Create(T0, Enumerable.Repeat((byte)0xFF, 10).ToArray()));

    [Fact]
    public void Create_RejectsWrongRandomLength() => Assert.Throws<ArgumentException>(() => UlidText.Create(T0, new byte[9]));

    [Fact]
    public void Create_RejectsTimeBefore1970() =>
        Assert.Throws<ArgumentException>(() => UlidText.Create(DateTimeOffset.FromUnixTimeMilliseconds(-1), new byte[10]));

    [Fact]
    public void Create_ProducesValidUlid() => Assert.True(UlidText.IsValid(UlidText.Create(T0, new byte[10])));

    [Theory]
    [InlineData("01JD4X7Q8N9V2K3M4P5R6S7T8W", true)]
    [InlineData("01JD4X7Q8N9V2K3M4P5R6S7T8I", false)]
    [InlineData("81JD4X7Q8N9V2K3M4P5R6S7T8W", false)]
    [InlineData("01jd4x7q8n9v2k3m4p5r6s7t8w", false)]
    [InlineData("01JD4X7Q8N9V2K3M4P5R6S7T8W\n", false)]
    [InlineData("01JD4X7Q8N9V2K3M4P5R6S7T8", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValid(string? value, bool ok) => Assert.Equal(ok, UlidText.IsValid(value));

    [Fact]
    public void Generator_UsesClockAndRandomness()
    {
        UlidGenerator generator = new(new FakeTimeProvider(T0));
        string a = generator.Next();
        string b = generator.Next();
        Assert.NotEqual(a, b);
        Assert.Equal(a[..10], b[..10]);
        Assert.True(UlidText.IsValid(a));
    }

    [Fact]
    public void IdTypes_ValidateThroughUlidText()
    {
        const string valid = "01JD4X7Q8N9V2K3M4P5R6S7T8W";
        Assert.True(ClientAnswerId.TryCreate(valid, out ClientAnswerId answer) && answer.Value == valid);
        Assert.True(MockSessionId.TryCreate(valid, out MockSessionId mock) && mock.Value == valid);
        Assert.True(UserId.TryCreate(valid, out UserId user) && user.Value == valid);
        Assert.False(ClientAnswerId.TryCreate("nope", out _));
        Assert.False(MockSessionId.TryCreate(null, out _));
        Assert.False(UserId.TryCreate("01JD4X7Q8N9V2K3M4P5R6S7T8W\n", out _));
        Assert.True(QuestionId.TryCreate(valid, out _));
    }

    [Fact]
    public void Roles_AreReadOnly()
    {
        Assert.Equal(["Admin", "Editor"], Roles.All);
        IList<string> list = Assert.IsAssignableFrom<IList<string>>(Roles.All);
        Assert.True(list.IsReadOnly);
    }
}
