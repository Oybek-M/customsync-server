using System.Text.Json.Nodes;
using CustomSync.Capture.Capture;
using CustomSync.Capture.Tdlib;
using Xunit;

namespace CustomSync.Tests;

/// <summary>
/// Plan 05 Task 4d tekshiruvi (TeamLead). Delegate testlari story
/// update'iga `chat_id` ni yuqori darajada ham qo'ygani uchun handler
/// faqat o'sha joydan o'qisa ham o'tib ketardi — haqiqiy TDLib'da u yerda
/// `chat_id` yo'q.
/// </summary>
public class CaptureProfilePhotoVerificationTests : IDisposable
{
    private readonly List<string> _paths = new();

    private string TempDb()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cs-4d-verify-{Guid.NewGuid():N}.db");
        _paths.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var p in _paths)
        {
            foreach (var f in new[] { p, p + "-wal", p + "-shm" })
            {
                try { if (File.Exists(f)) File.Delete(f); } catch (IOException) { }
            }
        }
    }

    [Fact]
    public void V01_Real_TDLib_story_update_shape_records_moments_and_story_field()
    {
        const long now = 1700500000;
        var clock = new TestTimeProvider(DateTimeOffset.FromUnixTimeSeconds(now));
        var cache = new MessageCache(TempDb(), clock);
        cache.Initialize();
        var handler = new CaptureUpdateHandler(cache, null, new CustomActivityScope((_, _) => true), clock, accountId: "1");

        // td_api.tl: `updateChatActiveStories active_stories:chatActiveStories`
        // — yagona maydon; `chat_id` faqat `chatActiveStories` ichida.
        handler.HandleUpdate("""
        {"@type":"updateChatActiveStories","active_stories":{"@type":"chatActiveStories","chat_id":5101,
         "list":{"@type":"storyListMain"},"order":1,"can_be_archived":true,"max_read_story_id":0,
         "stories":[{"@type":"storyInfo","story_id":1,"date":1700490000,"is_for_close_friends":false},
                    {"@type":"storyInfo","story_id":2,"date":1700495000,"is_for_close_friends":false}]}}
        """);

        Assert.True(cache.HasActivityEntryAt("5101", "status", 1700490000));
        Assert.True(cache.HasActivityEntryAt("5101", "status", 1700495000));
        var story = cache.GetLatestActivity("5101", "story");
        Assert.True(story.Exists);
        Assert.Equal("1700495000", story.Value);
    }

    [Fact]
    public async Task V02_Lookup_loop_survives_a_failing_item_and_logs_no_ids()
    {
        const long now = 1700600000;
        var clock = new TestTimeProvider(DateTimeOffset.FromUnixTimeSeconds(now));
        var cache = new MessageCache(TempDb(), clock);
        cache.Initialize();

        var transport = new CaptureContractTestBase.QueueTransport
        {
            Reply = req => req["user_id"]!.GetValue<long>() == 7001
                // Buzuq javob: `added_date` son emas — o'qishda istisno chiqadi.
                ? new JsonObject
                {
                    ["@type"] = "chatPhotos",
                    ["total_count"] = 1,
                    ["photos"] = new JsonArray { new JsonObject { ["id"] = "501", ["added_date"] = "not-a-number" } }
                }
                : new JsonObject
                {
                    ["@type"] = "chatPhotos",
                    ["total_count"] = 1,
                    ["photos"] = new JsonArray { new JsonObject { ["id"] = "502", ["added_date"] = now - 100 } }
                }
        };
        var logger = new CapturingLogger<ProfilePhotoLookup>();
        var lookup = new ProfilePhotoLookup(new TdClient(transport), cache, clock, logger);

        using var cts = new CancellationTokenSource();
        lookup.Start(cts.Token);
        try
        {
            Assert.True(lookup.Enqueue("7001", "501", "1"));
            Assert.True(lookup.Enqueue("7002", "502", "1"));

            // Birinchi buzuq javob siklni o'ldirsa, ikkinchisi hech qachon ishlanmaydi.
            Assert.True(await CaptureContractTestBase.WaitAsync(
                () => cache.HasActivityEntryAt("7002", "status", now - 100), 5000));
        }
        finally
        {
            cts.Cancel();
        }

        Assert.DoesNotContain(logger.Logs, l => l.Message.Contains("7001") || l.Message.Contains("501"));
    }
}
