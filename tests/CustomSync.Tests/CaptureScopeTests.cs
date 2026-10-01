using System.Collections.Concurrent;
using System.Text.Json;
using CustomSync.Capture.Capture;
using CustomSync.Capture.Preflight;
using CustomSync.Capture.Tdlib;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CustomSync.Tests;

public class TestSyncedScopeSettingsSource : ISyncedScopeSettingsSource
{
    private volatile ScopeSettingsSnapshot? _snapshot;

    public ScopeSettingsSnapshot? CurrentSnapshot
    {
        get => _snapshot;
        set => _snapshot = value;
    }
}

public class CaptureScopeTests
{
    private const string UserPeerId = "7053823996";
    // Group: 123456789 | (1 << 48)
    private static readonly string GroupPeerId = (123456789UL | (1UL << 48)).ToString();
    // Channel / Supergroup: 2827825432 + 2^49
    private const string ChannelPeerId = "562952781246744";
    // Type byte 3: (3 << 48) | 12345
    private static readonly string Type3PeerId = ((3UL << 48) | 12345UL).ToString();
    // High bit set above bit 55 (bit 56): ((1 << 56) | (1 << 48) | 999) -> Group type
    private static readonly string HighBitGroupPeerId = ((1UL << 56) | (1UL << 48) | 999UL).ToString();

    private static string CreateTempDbPath() =>
        Path.Combine(Path.GetTempPath(), $"customsync_scope_test_{Guid.NewGuid():N}.db");

    [Fact]
    public void Test01_Chain_truth_table_exact_and_categories_and_overrides()
    {
        // 1. Exact BL beats WL category
        var snapshot1 = new ScopeSettingsSnapshot(
            blocklist: new HashSet<string> { UserPeerId },
            whitelistCategories: new ScopeCategories(User: true));
        var eval1 = new CaptureScopeEvaluator(serverBlock: null, serverAllow: null, settingsSource: new DirectSnapshotSource(snapshot1));
        Assert.False(eval1.ShouldAntiDelete(UserPeerId));
        Assert.False(eval1.ShouldAntiEdit(UserPeerId));
        Assert.False(eval1.ShouldCache(UserPeerId));

        // 2. Exact WL beats BL category
        var snapshot2 = new ScopeSettingsSnapshot(
            whitelist: new HashSet<string> { ChannelPeerId },
            blocklistCategories: new ScopeCategories(Channel: true));
        var eval2 = new CaptureScopeEvaluator(serverBlock: null, serverAllow: null, settingsSource: new DirectSnapshotSource(snapshot2));
        Assert.True(eval2.ShouldAntiDelete(ChannelPeerId));
        Assert.True(eval2.ShouldAntiEdit(ChannelPeerId));
        Assert.True(eval2.ShouldCache(ChannelPeerId));

        // 3. Category applies by type for user/group/channel
        var snapshot3 = new ScopeSettingsSnapshot(
            whitelistCategories: new ScopeCategories(User: true, Channel: true),
            blocklistCategories: new ScopeCategories(Group: true));
        var eval3 = new CaptureScopeEvaluator(serverBlock: null, serverAllow: null, settingsSource: new DirectSnapshotSource(snapshot3));
        Assert.True(eval3.ShouldCache(UserPeerId));
        Assert.False(eval3.ShouldCache(GroupPeerId));
        Assert.True(eval3.ShouldCache(ChannelPeerId));

        // 4. Type >= 3 gets no category
        var snapshot4 = new ScopeSettingsSnapshot(
            whitelistCategories: new ScopeCategories(User: true, Group: true, Channel: true),
            globalAntiDelete: false,
            globalAntiEdit: false);
        var eval4 = new CaptureScopeEvaluator(serverBlock: null, serverAllow: null, settingsSource: new DirectSnapshotSource(snapshot4));
        Assert.False(eval4.ShouldCache(Type3PeerId));
        Assert.False(eval4.ShouldAntiDelete(Type3PeerId));
        Assert.False(eval4.ShouldAntiEdit(Type3PeerId));

        // 5. Per-peer true overrides global false and per-peer false overrides global true
        const string peerA = "1001";
        const string peerB = "1002";
        const string peerC = "1003";
        const string peerD = "1004";

        var snapshot5 = new ScopeSettingsSnapshot(
            antiDeletePerPeer: new Dictionary<string, bool>
            {
                [peerA] = true,
                [peerB] = false
            },
            antiEditPerPeer: new Dictionary<string, bool>
            {
                [peerC] = true,
                [peerD] = false
            },
            globalAntiDelete: false,
            globalAntiEdit: false);

        var eval5 = new CaptureScopeEvaluator(serverBlock: null, serverAllow: null, settingsSource: new DirectSnapshotSource(snapshot5));
        Assert.True(eval5.ShouldAntiDelete(peerA)); // per-peer true overrides global false
        Assert.False(eval5.ShouldAntiDelete(peerB));

        var snapshot5b = new ScopeSettingsSnapshot(
            antiDeletePerPeer: new Dictionary<string, bool>
            {
                [peerA] = true,
                [peerB] = false
            },
            antiEditPerPeer: new Dictionary<string, bool>
            {
                [peerC] = true,
                [peerD] = false
            },
            globalAntiDelete: true,
            globalAntiEdit: true);

        var eval5b = new CaptureScopeEvaluator(serverBlock: null, serverAllow: null, settingsSource: new DirectSnapshotSource(snapshot5b));
        Assert.False(eval5b.ShouldAntiDelete(peerB)); // per-peer false overrides global true
        Assert.True(eval5b.ShouldAntiEdit(peerC));   // per-peer true overrides global false (in 5a)
        Assert.False(eval5b.ShouldAntiEdit(peerD));  // per-peer false overrides global true

        // 6. Cache = delete-fallback OR edit-fallback
        const string deleteOnlyPeer = "2001";
        const string editOnlyPeer = "2002";
        const string nonePeer = "2003";

        var snapshot6 = new ScopeSettingsSnapshot(
            antiDeletePerPeer: new Dictionary<string, bool>
            {
                [deleteOnlyPeer] = true,
                [editOnlyPeer] = false,
                [nonePeer] = false
            },
            antiEditPerPeer: new Dictionary<string, bool>
            {
                [deleteOnlyPeer] = false,
                [editOnlyPeer] = true,
                [nonePeer] = false
            },
            globalAntiDelete: false,
            globalAntiEdit: false);

        var eval6 = new CaptureScopeEvaluator(serverBlock: null, serverAllow: null, settingsSource: new DirectSnapshotSource(snapshot6));
        Assert.True(eval6.ShouldCache(deleteOnlyPeer));
        Assert.True(eval6.ShouldAntiDelete(deleteOnlyPeer));
        Assert.False(eval6.ShouldAntiEdit(deleteOnlyPeer));

        Assert.True(eval6.ShouldCache(editOnlyPeer));
        Assert.False(eval6.ShouldAntiDelete(editOnlyPeer));
        Assert.True(eval6.ShouldAntiEdit(editOnlyPeer));

        Assert.False(eval6.ShouldCache(nonePeer));
        Assert.False(eval6.ShouldAntiDelete(nonePeer));
        Assert.False(eval6.ShouldAntiEdit(nonePeer));
    }

    [Fact]
    public void Test02_Peer_type_computation_and_mask_with_0xFF()
    {
        Assert.Equal(PeerType.User, CaptureScopeEvaluator.GetPeerType(UserPeerId));
        Assert.Equal(PeerType.Group, CaptureScopeEvaluator.GetPeerType(GroupPeerId));
        Assert.Equal(PeerType.Channel, CaptureScopeEvaluator.GetPeerType(ChannelPeerId));
        Assert.Equal(PeerType.Unknown, CaptureScopeEvaluator.GetPeerType(Type3PeerId));
        Assert.Equal(PeerType.Unknown, CaptureScopeEvaluator.GetPeerType("invalid"));
        Assert.Equal(PeerType.Unknown, CaptureScopeEvaluator.GetPeerType(""));

        // Peer with bit 56 set: shifted right 48 bits gives 257.
        // With & 0xFF, (257 & 0xFF) == 1 (Group).
        // Without & 0xFF, (257) would be Unknown!
        Assert.Equal(PeerType.Group, CaptureScopeEvaluator.GetPeerType(HighBitGroupPeerId));

        var snapshot = new ScopeSettingsSnapshot(
            whitelistCategories: new ScopeCategories(Group: true));
        var eval = new CaptureScopeEvaluator(serverBlock: null, serverAllow: null, settingsSource: new DirectSnapshotSource(snapshot));
        Assert.True(eval.ShouldCache(HighBitGroupPeerId));
    }

    [Fact]
    public void Test03_Layers_server_block_and_allow_precedence_and_default_enabled()
    {
        // 1. Server Block beats synced WL
        var snapshotWithWl = new ScopeSettingsSnapshot(whitelist: new HashSet<string> { ChannelPeerId });
        var evalBlock = new CaptureScopeEvaluator(
            serverBlock: new[] { ChannelPeerId },
            serverAllow: null,
            settingsSource: new DirectSnapshotSource(snapshotWithWl));

        Assert.False(evalBlock.ShouldCache(ChannelPeerId));
        Assert.False(evalBlock.ShouldAntiDelete(ChannelPeerId));
        Assert.False(evalBlock.ShouldAntiEdit(ChannelPeerId));

        // 2. Server Allow beats synced BL
        var snapshotWithBl = new ScopeSettingsSnapshot(blocklist: new HashSet<string> { UserPeerId });
        var evalAllow = new CaptureScopeEvaluator(
            serverBlock: null,
            serverAllow: new[] { UserPeerId },
            settingsSource: new DirectSnapshotSource(snapshotWithBl));

        Assert.True(evalAllow.ShouldCache(UserPeerId));
        Assert.True(evalAllow.ShouldAntiDelete(UserPeerId));
        Assert.True(evalAllow.ShouldAntiEdit(UserPeerId));

        // 3. Server Block beats Server Allow if same peer present in evaluator
        const string bothPeer = "999888777";
        var evalConflict = new CaptureScopeEvaluator(
            serverBlock: new[] { bothPeer },
            serverAllow: new[] { bothPeer },
            settingsSource: null);

        Assert.False(evalConflict.ShouldCache(bothPeer));
        Assert.False(evalConflict.ShouldAntiDelete(bothPeer));
        Assert.False(evalConflict.ShouldAntiEdit(bothPeer));

        // 4. Snapshot null -> DefaultEnabled
        var evalDefaultFalse = new CaptureScopeEvaluator(
            serverBlock: null,
            serverAllow: null,
            serverDefaultEnabled: false,
            settingsSource: new NullSyncedScopeSettingsSource());

        Assert.False(evalDefaultFalse.ShouldCache(UserPeerId));
        Assert.False(evalDefaultFalse.ShouldAntiDelete(UserPeerId));
        Assert.False(evalDefaultFalse.ShouldAntiEdit(UserPeerId));

        var evalDefaultTrue = new CaptureScopeEvaluator(
            serverBlock: null,
            serverAllow: null,
            serverDefaultEnabled: true,
            settingsSource: new NullSyncedScopeSettingsSource());

        Assert.True(evalDefaultTrue.ShouldCache(UserPeerId));
        Assert.True(evalDefaultTrue.ShouldAntiDelete(UserPeerId));
        Assert.True(evalDefaultTrue.ShouldAntiEdit(UserPeerId));
    }

    [Fact]
    public void Test04_Snapshot_swapped_at_runtime_changes_next_decision()
    {
        var source = new TestSyncedScopeSettingsSource();
        var evaluator = new CaptureScopeEvaluator(
            serverBlock: null,
            serverAllow: null,
            serverDefaultEnabled: false,
            settingsSource: source);

        // Initially source is null -> DefaultEnabled (false)
        Assert.False(evaluator.ShouldCache(UserPeerId));

        // Snapshot 1: Whitelist contains UserPeerId
        source.CurrentSnapshot = new ScopeSettingsSnapshot(whitelist: new HashSet<string> { UserPeerId });
        Assert.True(evaluator.ShouldCache(UserPeerId));
        Assert.True(evaluator.ShouldAntiDelete(UserPeerId));

        // Snapshot 2: Blocklist contains UserPeerId
        source.CurrentSnapshot = new ScopeSettingsSnapshot(blocklist: new HashSet<string> { UserPeerId });
        Assert.False(evaluator.ShouldCache(UserPeerId));
        Assert.False(evaluator.ShouldAntiDelete(UserPeerId));
    }

    [Fact]
    public void Test05b_Preflight_rejects_entries_that_parse_but_can_never_match()
    {
        // Evaluator satrlarni ANIQ solishtiradi. long.TryParse o'tkazadigan,
        // lekin TdIdMapper hech qachon chiqarmaydigan shakl Block'da turib,
        // chatni jimgina ushlatib yuboradi. Eng ehtimoliy xato — TDLib /
        // Bot API chat_id (-100...) ni yozish.
        foreach (var bad in new[] { "-1002827825432", "0123", " 7053823996", "7053823996 ", "+7053823996", "0" })
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Telegram:ApiId"] = "12345",
                ["Telegram:ApiHash"] = "hash",
                ["Telegram:DatabaseDirectory"] = Path.GetTempPath(),
                ["Telegram:FilesDirectory"] = Path.GetTempPath(),
                ["Capture:CacheDatabasePath"] = Path.Combine(Path.GetTempPath(), $"cs-pf-{Guid.NewGuid():N}.db"),
                ["Capture:Scope:Block:0"] = bad
            }).Build();

            var report = CapturePreflight.Check(config, nativeLibChecker: _ => true);
            Assert.False(report.Success, $"'{bad}' must be rejected");
            Assert.Contains(report.Errors, e => e.Contains("Capture:Scope:Block"));
        }

        foreach (var bad in new[] { "ture", "1", "yes" })
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Telegram:ApiId"] = "12345",
                ["Telegram:ApiHash"] = "hash",
                ["Telegram:DatabaseDirectory"] = Path.GetTempPath(),
                ["Telegram:FilesDirectory"] = Path.GetTempPath(),
                ["Capture:CacheDatabasePath"] = Path.Combine(Path.GetTempPath(), $"cs-pf-{Guid.NewGuid():N}.db"),
                ["Capture:Scope:DefaultEnabled"] = bad
            }).Build();

            var report = CapturePreflight.Check(config, nativeLibChecker: _ => true);
            Assert.False(report.Success, $"DefaultEnabled '{bad}' must be rejected");
            Assert.Contains(report.Errors, e => e.Contains("Capture:Scope:DefaultEnabled"));
        }
    }

    [Fact]
    public void Test05_Preflight_validation_scope_configuration()
    {
        // 1. Malformed entry in Block
        var configMalformedBlock = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Telegram:ApiId"] = "12345",
            ["Telegram:ApiHash"] = "hash",
            ["Telegram:DatabaseDirectory"] = Path.GetTempPath(),
            ["Telegram:FilesDirectory"] = Path.GetTempPath(),
            ["Capture:CacheDatabasePath"] = Path.Combine(Path.GetTempPath(), $"cs-pf-{Guid.NewGuid():N}.db"),
            ["Capture:Scope:Block:0"] = "not_a_valid_number"
        }).Build();

        var report1 = CapturePreflight.Check(configMalformedBlock, nativeLibChecker: _ => true);
        Assert.False(report1.Success);
        Assert.Contains(report1.Errors, e => e.Contains("Capture:Scope:Block"));
        // Peer text/entry must NOT be logged or included in error message!
        Assert.DoesNotContain(report1.Errors, e => e.Contains("not_a_valid_number"));

        // 2. Malformed entry in Allow
        var configMalformedAllow = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Telegram:ApiId"] = "12345",
            ["Telegram:ApiHash"] = "hash",
            ["Telegram:DatabaseDirectory"] = Path.GetTempPath(),
            ["Telegram:FilesDirectory"] = Path.GetTempPath(),
            ["Capture:CacheDatabasePath"] = Path.Combine(Path.GetTempPath(), $"cs-pf-{Guid.NewGuid():N}.db"),
            ["Capture:Scope:Allow:0"] = "invalid_allow_id"
        }).Build();

        var report2 = CapturePreflight.Check(configMalformedAllow, nativeLibChecker: _ => true);
        Assert.False(report2.Success);
        Assert.Contains(report2.Errors, e => e.Contains("Capture:Scope:Allow"));
        Assert.DoesNotContain(report2.Errors, e => e.Contains("invalid_allow_id"));

        // 3. Same peer in Block and Allow
        var configOverlap = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Telegram:ApiId"] = "12345",
            ["Telegram:ApiHash"] = "hash",
            ["Telegram:DatabaseDirectory"] = Path.GetTempPath(),
            ["Telegram:FilesDirectory"] = Path.GetTempPath(),
            ["Capture:CacheDatabasePath"] = Path.Combine(Path.GetTempPath(), $"cs-pf-{Guid.NewGuid():N}.db"),
            ["Capture:Scope:Block:0"] = UserPeerId,
            ["Capture:Scope:Allow:0"] = UserPeerId
        }).Build();

        var report3 = CapturePreflight.Check(configOverlap, nativeLibChecker: _ => true);
        Assert.False(report3.Success);
        Assert.Contains(report3.Errors, e => e.Contains("overlapping"));
        Assert.DoesNotContain(report3.Errors, e => e.Contains(UserPeerId));

        // 4. Valid config
        var configValid = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Telegram:ApiId"] = "12345",
            ["Telegram:ApiHash"] = "hash",
            ["Telegram:DatabaseDirectory"] = Path.GetTempPath(),
            ["Telegram:FilesDirectory"] = Path.GetTempPath(),
            ["Capture:CacheDatabasePath"] = Path.Combine(Path.GetTempPath(), $"cs-pf-{Guid.NewGuid():N}.db"),
            ["Capture:Scope:Block:0"] = "111111",
            ["Capture:Scope:Allow:0"] = "222222",
            ["Capture:Scope:DefaultEnabled"] = "false"
        }).Build();

        var report4 = CapturePreflight.Check(configValid, nativeLibChecker: _ => true);
        Assert.True(report4.Success);
    }

    [Fact]
    public void Test06_Handler_AntiDelete_on_AntiEdit_off_caches_updates_and_emits_delete_with_edited_text()
    {
        var dbPath = CreateTempDbPath();
        var timeProvider = new TestTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1787111222));
        var cache = new MessageCache(dbPath, timeProvider);
        cache.Initialize();

        // Scope: ShouldCache = true, ShouldAntiDelete = true, ShouldAntiEdit = false
        var scope = new CustomRuleScope(
            shouldCache: _ => true,
            shouldAntiDelete: _ => true,
            shouldAntiEdit: _ => false);

        var handler = new CaptureUpdateHandler(cache, scope, timeProvider, accountId: "7053823996");

        long chatId = -1002827825432L;
        long tdlibId = 100L << 20;

        // 1. Initial message
        var newMsgJson = $@"{{
            ""@type"": ""updateNewMessage"",
            ""message"": {{
                ""id"": {tdlibId},
                ""chat_id"": {chatId},
                ""sender_id"": {{ ""@type"": ""messageSenderUser"", ""user_id"": 7053823996 }},
                ""is_outgoing"": false,
                ""date"": 1787000000,
                ""content"": {{
                    ""@type"": ""messageText"",
                    ""text"": {{ ""text"": ""Dastlabki matn"" }}
                }}
            }}
        }}";

        handler.HandleUpdate(newMsgJson);
        var cached = cache.Get(chatId, 100L);
        Assert.NotNull(cached);
        Assert.Equal("Dastlabki matn", cached.Text);

        // 2. Edit message: ShouldAntiEdit is false, but ShouldCache is true
        var editJson = $@"{{
            ""@type"": ""updateMessageContent"",
            ""chat_id"": {chatId},
            ""message_id"": {tdlibId},
            ""new_content"": {{
                ""@type"": ""messageText"",
                ""text"": {{ ""text"": ""Tahrirlangan matn"" }}
            }}
        }}";

        handler.HandleUpdate(editJson);

        // Edit row must NOT be emitted
        var editedRows = cache.GetOutboxRows("edited");
        Assert.Empty(editedRows);

        // But cache MUST hold the new text
        var cachedAfterEdit = cache.Get(chatId, 100L);
        Assert.NotNull(cachedAfterEdit);
        Assert.Equal("Tahrirlangan matn", cachedAfterEdit.Text);

        // 3. Delete message: ShouldAntiDelete is true
        var deleteJson = $@"{{
            ""@type"": ""updateDeleteMessages"",
            ""chat_id"": {chatId},
            ""message_ids"": [{tdlibId}],
            ""is_permanent"": true,
            ""from_cache"": false
        }}";

        handler.HandleUpdate(deleteJson);

        // Deletion row IS emitted and carries the edited text
        var deletedRows = cache.GetOutboxRows("deleted");
        Assert.Single(deletedRows);
        Assert.Contains("Tahrirlangan matn", deletedRows[0].PayloadJson);
    }

    [Fact]
    public void Test07_Handler_AntiEdit_on_AntiDelete_off_emits_edit_and_ignores_delete()
    {
        var dbPath = CreateTempDbPath();
        var timeProvider = new TestTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1787111222));
        var cache = new MessageCache(dbPath, timeProvider);
        cache.Initialize();

        // Scope: ShouldCache = true, ShouldAntiDelete = false, ShouldAntiEdit = true
        var scope = new CustomRuleScope(
            shouldCache: _ => true,
            shouldAntiDelete: _ => false,
            shouldAntiEdit: _ => true);

        var handler = new CaptureUpdateHandler(cache, scope, timeProvider, accountId: "7053823996");

        long chatId = -1002827825432L;
        long tdlibId = 200L << 20;

        var newMsgJson = $@"{{
            ""@type"": ""updateNewMessage"",
            ""message"": {{
                ""id"": {tdlibId},
                ""chat_id"": {chatId},
                ""sender_id"": {{ ""@type"": ""messageSenderUser"", ""user_id"": 7053823996 }},
                ""is_outgoing"": false,
                ""date"": 1787000000,
                ""content"": {{
                    ""@type"": ""messageText"",
                    ""text"": {{ ""text"": ""Avvalgi matn"" }}
                }}
            }}
        }}";

        handler.HandleUpdate(newMsgJson);

        var editJson = $@"{{
            ""@type"": ""updateMessageContent"",
            ""chat_id"": {chatId},
            ""message_id"": {tdlibId},
            ""new_content"": {{
                ""@type"": ""messageText"",
                ""text"": {{ ""text"": ""Yangi matn"" }}
            }}
        }}";

        handler.HandleUpdate(editJson);
        handler.HandleUpdate($@"{{
            ""@type"": ""updateMessageEdited"",
            ""chat_id"": {chatId},
            ""message_id"": {tdlibId},
            ""edit_date"": 1787111300
        }}");

        // Edit row IS emitted
        var editedRows = cache.GetOutboxRows("edited");
        Assert.Single(editedRows);
        Assert.Contains("Yangi matn", editedRows[0].PayloadJson);

        // Delete message
        var deleteJson = $@"{{
            ""@type"": ""updateDeleteMessages"",
            ""chat_id"": {chatId},
            ""message_ids"": [{tdlibId}],
            ""is_permanent"": true,
            ""from_cache"": false
        }}";

        handler.HandleUpdate(deleteJson);

        // Deletion row is NOT emitted
        var deletedRows = cache.GetOutboxRows("deleted");
        Assert.Empty(deletedRows);
    }

    [Fact]
    public void Test08_Handler_out_of_scope_chat_edit_with_cache_miss_sends_no_getMessage()
    {
        var dbPath = CreateTempDbPath();
        var timeProvider = new TestTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1787111222));
        var cache = new MessageCache(dbPath, timeProvider);
        cache.Initialize();

        // Scope: all false (out-of-scope)
        var scope = new NoneCaptureScope();
        var fakeTransport = new FakeTdTransport();
        using var client = new TdClient(fakeTransport);

        var handler = new CaptureUpdateHandler(cache, scope, timeProvider, accountId: "7053823996");
        handler.Attach(client);

        long chatId = 7053823996L;
        long tdlibId = 300L << 20;

        var editJson = $@"{{
            ""@type"": ""updateMessageContent"",
            ""chat_id"": {chatId},
            ""message_id"": {tdlibId},
            ""new_content"": {{
                ""@type"": ""messageText"",
                ""text"": {{ ""text"": ""Begona xabar tahriri"" }}
            }}
        }}";

        handler.HandleUpdate(editJson);

        // No getMessage request must be sent!
        Assert.Empty(fakeTransport.OutgoingRequests);
    }

    [Fact]
    public void Test09_Production_wiring_default_config_writes_nothing()
    {
        var dbPath = CreateTempDbPath();
        var fakeTransport = new FakeTdTransport();

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Capture:CacheDatabasePath"] = dbPath
        }).Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddMessageCache(config);
        services.AddSingleton<ITdTransport>(fakeTransport);
        services.AddSingleton<ITdClient, TdClient>();
        services.AddCaptureHandlers();

        using var sp = services.BuildServiceProvider();

        // Resolve only ITdClient (like Worker.cs does)
        var client = sp.GetRequiredService<ITdClient>();
        var cache = sp.GetRequiredService<MessageCache>();
        cache.Initialize();

        // Set account id via updateOption
        fakeTransport.EnqueueIncoming(@"{""@type"":""updateOption"",""name"":""my_id"",""value"":{""@type"":""optionValueInteger"",""value"":7053823996}}");

        long chatId = -1002827825432L;
        long tdlibId = 400L << 20;

        // Deliver new message and delete
        fakeTransport.EnqueueIncoming($@"{{
            ""@type"": ""updateNewMessage"",
            ""message"": {{
                ""id"": {tdlibId},
                ""chat_id"": {chatId},
                ""sender_id"": {{ ""@type"": ""messageSenderUser"", ""user_id"": 7053823996 }},
                ""is_outgoing"": false,
                ""date"": 1787000000,
                ""content"": {{
                    ""@type"": ""messageText"",
                    ""text"": {{ ""text"": ""Default scope test"" }}
                }}
            }}
        }}");

        fakeTransport.EnqueueIncoming($@"{{
            ""@type"": ""updateDeleteMessages"",
            ""chat_id"": {chatId},
            ""message_ids"": [{tdlibId}],
            ""is_permanent"": true,
            ""from_cache"": false
        }}");

        // Wait small amount for TdClient thread
        Thread.Sleep(200);

        // Fail-closed default config: nothing cached, nothing written to outbox
        Assert.Null(cache.Get(chatId, 400L));
        Assert.Empty(cache.GetOutboxRows());
    }

    [Fact]
    public void Test10b_Production_wiring_uses_the_registered_synced_settings_source()
    {
        // Task 6 haqiqiy manbani DI'ga qo'yadi. Registratsiya uni e'tiborsiz
        // qoldirsa, egasining tdesktop sozlamalari jimgina ishlamay qoladi.
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Capture:CacheDatabasePath"] = CreateTempDbPath()
        }).Build();

        foreach (var registerFirst in new[] { true, false })
        {
            var source = new DirectSnapshotSource(new ScopeSettingsSnapshot(
                whitelist: new HashSet<string> { ChannelPeerId }));
            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(config);
            services.AddMessageCache(config);
            services.AddSingleton<ITdTransport>(new FakeTdTransport());
            services.AddSingleton<ITdClient, TdClient>();
            if (registerFirst) services.AddSingleton<ISyncedScopeSettingsSource>(source);
            services.AddCaptureHandlers();
            if (!registerFirst) services.AddSingleton<ISyncedScopeSettingsSource>(source);

            using var sp = services.BuildServiceProvider();
            var scope = sp.GetRequiredService<ICaptureScope>();
            Assert.True(scope.ShouldAntiDelete(ChannelPeerId), $"registerFirst={registerFirst}");
            Assert.False(scope.ShouldAntiDelete(UserPeerId), $"registerFirst={registerFirst}");
        }
    }

    [Fact]
    public void Test10_Production_wiring_CaptureScopeAllow_contains_chat_writes_deletion_row()
    {
        var dbPath = CreateTempDbPath();
        var fakeTransport = new FakeTdTransport();

        // Allow ChannelPeerId ("562952781246744")
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Capture:CacheDatabasePath"] = dbPath,
            ["Capture:Scope:Allow:0"] = ChannelPeerId
        }).Build();

        var logger = new TestLogger<CaptureUpdateHandler>();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton<ILogger<CaptureUpdateHandler>>(logger);
        services.AddMessageCache(config);
        services.AddSingleton<ITdTransport>(fakeTransport);
        services.AddSingleton<ITdClient, TdClient>();
        services.AddCaptureHandlers();

        using var sp = services.BuildServiceProvider();

        // Resolve only ITdClient
        var client = sp.GetRequiredService<ITdClient>();
        var cache = sp.GetRequiredService<MessageCache>();
        cache.Initialize();

        fakeTransport.EnqueueIncoming(@"{""@type"":""updateOption"",""name"":""my_id"",""value"":{""@type"":""optionValueInteger"",""value"":7053823996}}");

        long chatId = -1002827825432L;
        long tdlibId = 500L << 20;

        fakeTransport.EnqueueIncoming($@"{{
            ""@type"": ""updateNewMessage"",
            ""message"": {{
                ""id"": {tdlibId},
                ""chat_id"": {chatId},
                ""sender_id"": {{ ""@type"": ""messageSenderUser"", ""user_id"": 7053823996 }},
                ""is_outgoing"": false,
                ""date"": 1787000000,
                ""content"": {{
                    ""@type"": ""messageText"",
                    ""text"": {{ ""text"": ""Allow test message"" }}
                }}
            }}
        }}");

        fakeTransport.EnqueueIncoming($@"{{
            ""@type"": ""updateDeleteMessages"",
            ""chat_id"": {chatId},
            ""message_ids"": [{tdlibId}],
            ""is_permanent"": true,
            ""from_cache"": false
        }}");

        var deadline = DateTime.UtcNow.AddSeconds(5);
        IReadOnlyList<OutboxRow> rows = Array.Empty<OutboxRow>();
        while (DateTime.UtcNow < deadline)
        {
            rows = cache.GetOutboxRows("deleted");
            if (rows.Count > 0) break;
            Thread.Sleep(20);
        }

        Assert.Single(rows);
        Assert.Equal("deleted", rows[0].Kind);
        Assert.Equal(ChannelPeerId, rows[0].PeerId);
        Assert.Equal(500L, rows[0].MsgId);
    }

    [Fact]
    public void Test11_Logs_captured_during_wiring_tests_contain_no_peer_ids()
    {
        var dbPath = CreateTempDbPath();
        var fakeTransport = new FakeTdTransport();
        var logger = new TestLogger<CaptureUpdateHandler>();

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Capture:CacheDatabasePath"] = dbPath,
            ["Capture:Scope:Allow:0"] = ChannelPeerId
        }).Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton<ILogger<CaptureUpdateHandler>>(logger);
        services.AddMessageCache(config);
        services.AddSingleton<ITdTransport>(fakeTransport);
        services.AddSingleton<ITdClient, TdClient>();
        services.AddCaptureHandlers();

        using var sp = services.BuildServiceProvider();
        var client = sp.GetRequiredService<ITdClient>();
        var cache = sp.GetRequiredService<MessageCache>();
        cache.Initialize();

        fakeTransport.EnqueueIncoming(@"{""@type"":""updateOption"",""name"":""my_id"",""value"":{""@type"":""optionValueInteger"",""value"":7053823996}}");

        long chatId = -1002827825432L;
        long tdlibId = 600L << 20;

        fakeTransport.EnqueueIncoming($@"{{
            ""@type"": ""updateNewMessage"",
            ""message"": {{
                ""id"": {tdlibId},
                ""chat_id"": {chatId},
                ""sender_id"": {{ ""@type"": ""messageSenderUser"", ""user_id"": 7053823996 }},
                ""is_outgoing"": false,
                ""date"": 1787000000,
                ""content"": {{
                    ""@type"": ""messageText"",
                    ""text"": {{ ""text"": ""Secret text"" }}
                }}
            }}
        }}");

        fakeTransport.EnqueueIncoming($@"{{
            ""@type"": ""updateDeleteMessages"",
            ""chat_id"": {chatId},
            ""message_ids"": [{tdlibId}],
            ""is_permanent"": true,
            ""from_cache"": false
        }}");

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var rows = cache.GetOutboxRows("deleted");
            if (rows.Count > 0) break;
            Thread.Sleep(20);
        }

        // Verify that none of the log messages contain the peer ID or message text
        foreach (var entry in logger.Entries)
        {
            Assert.DoesNotContain(ChannelPeerId, entry.Message);
            Assert.DoesNotContain("Secret text", entry.Message);
        }
    }

    private class DirectSnapshotSource(ScopeSettingsSnapshot snapshot) : ISyncedScopeSettingsSource
    {
        public ScopeSettingsSnapshot? CurrentSnapshot => snapshot;
    }

    private class CustomRuleScope(
        Func<string, bool> shouldCache,
        Func<string, bool> shouldAntiDelete,
        Func<string, bool> shouldAntiEdit) : ICaptureScope
    {
        public bool ShouldCache(string peerId) => shouldCache(peerId);
        public bool ShouldAntiDelete(string peerId) => shouldAntiDelete(peerId);
        public bool ShouldAntiEdit(string peerId) => shouldAntiEdit(peerId);
    }
}
