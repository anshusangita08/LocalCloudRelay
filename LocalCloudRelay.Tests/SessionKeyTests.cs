using System.Text;
using LocalCloudRelay;

namespace LocalCloudRelay.Tests;

/// <summary>
/// The session key decides which model a conversation stays on and therefore whether
/// its prompt cache is reused. It has to separate different chats and hold one chat
/// together, including across Claude Code's compaction.
/// </summary>
public sealed class SessionKeyTests
{
    private static byte[] Utf8(string json) => Encoding.UTF8.GetBytes(json);

    [Fact]
    public void ClaudeCodeSessionHeaderIsUsed()
    {
        var key = RelaySession.Key(name => name == "x-claude-code-session-id" ? "abc-123" : null, Utf8("{}"), "10.0.0.5");
        Assert.Equal("x-claude-code-session-id:abc-123", key);
    }

    [Fact]
    public void MetadataSessionInLegacyUserIdFormatIsUsed()
    {
        var body = Utf8("""{"metadata":{"user_id":"user_ab12_account_cd34_session_0f1e2d3c-aaaa-bbbb-cccc-1234567890ab"},"messages":[{"role":"user","content":"hi"}]}""");
        Assert.Equal("metadata:0f1e2d3c-aaaa-bbbb-cccc-1234567890ab", RelaySession.Key(_ => null, body, "10.0.0.5"));
    }

    [Fact]
    public void MetadataSessionInJsonUserIdFormatIsUsed()
    {
        var body = Utf8("""{"metadata":{"user_id":"{\"device_id\":\"d1\",\"session_id\":\"s-42\"}"},"messages":[{"role":"user","content":"hi"}]}""");
        Assert.Equal("metadata:s-42", RelaySession.Key(_ => null, body, "10.0.0.5"));
    }

    [Fact]
    public void CompactionKeepsTheSameKeyWhenMetadataNamesTheSession()
    {
        // Compaction replaces the opening message; the metadata session does not change.
        const string meta = """{"metadata":{"user_id":"user_x_account_y_session_11111111-2222-3333-4444-555555555555"},""";
        var before = Utf8(meta + """ "messages":[{"role":"user","content":"original opener"}]}""");
        var after = Utf8(meta + """ "messages":[{"role":"user","content":"This session is being continued from a previous conversation"}]}""");
        Assert.Equal(RelaySession.Key(_ => null, before, "ip"), RelaySession.Key(_ => null, after, "ip"));
    }

    [Fact]
    public void HeadersOutrankMetadata()
    {
        var body = Utf8("""{"metadata":{"user_id":"user_x_session_99999999-0000"},"messages":[]}""");
        Assert.Equal("x-relay-session-id:mine", RelaySession.Key(n => n == "x-relay-session-id" ? "mine" : null, body, "ip"));
    }

    [Fact]
    public void SystemRemindersDoNotMergeDifferentChats()
    {
        // Claude Code opens every chat with the same reminder block; only the real text differs.
        const string reminder = "<system-reminder>\\nAs you answer, use this context...\\n</system-reminder>";
        var chatOne = Utf8($$"""{"messages":[{"role":"user","content":[{"type":"text","text":"{{reminder}}"},{"type":"text","text":"fix the login bug"}]}]}""");
        var chatTwo = Utf8($$"""{"messages":[{"role":"user","content":[{"type":"text","text":"{{reminder}}"},{"type":"text","text":"write a README"}]}]}""");
        Assert.NotEqual(RelaySession.Key(_ => null, chatOne, "ip"), RelaySession.Key(_ => null, chatTwo, "ip"));
    }

    [Fact]
    public void RemindersInsideOneStringAreStripped()
    {
        var withReminder = Utf8("""{"messages":[{"role":"user","content":"<system-reminder>boilerplate</system-reminder>\nreal question"}]}""");
        var plain = Utf8("""{"messages":[{"role":"user","content":"real question"}]}""");
        Assert.Equal(RelaySession.ConversationFingerprint(plain), RelaySession.ConversationFingerprint(withReminder));
    }

    [Fact]
    public void AReminderOnlyOpenerFallsThroughToTheNextUserText()
    {
        var body = Utf8("""{"messages":[{"role":"user","content":"<system-reminder>only this</system-reminder>"},{"role":"assistant","content":"ok"},{"role":"user","content":"actual ask"}]}""");
        var expected = Utf8("""{"messages":[{"role":"user","content":"actual ask"}]}""");
        Assert.Equal(RelaySession.ConversationFingerprint(expected), RelaySession.ConversationFingerprint(body));
    }

    [Fact]
    public void MalformedMetadataIsIgnored()
    {
        Assert.Null(RelaySession.MetadataSessionId(Utf8("""{"metadata":{"user_id":"{not json"}}""")));
        Assert.Null(RelaySession.MetadataSessionId(Utf8("""{"metadata":{"user_id":"user_without_session"}}""")));
        Assert.Null(RelaySession.MetadataSessionId(Utf8("""{"metadata":"x"}""")));
        Assert.Null(RelaySession.MetadataSessionId(null));
    }
}
