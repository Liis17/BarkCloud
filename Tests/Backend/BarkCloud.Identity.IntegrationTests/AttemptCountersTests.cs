using BarkCloud.Identity.Persistence.Services;

using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Identity.IntegrationTests;

public class AttemptCountersTests
{
    [Fact]
    public async Task TryReserve_ParallelRequestsForNewKey_AllowExactlyMax()
    {
        const int max = 5;
        await using var database = await PostgresIdentityDatabase.CreateAsync();

        // Каждый запрос — свой контекст и соединение; ключа ещё нет, поэтому первые попытки гоняются за вставкой.
        var results = await Task.WhenAll(Enumerable.Range(0, 40).Select(async _ =>
        {
            await using var context = database.CreateContext();
            return await new AttemptCountersStorage(context).TryReserve("login:7", max, TimeSpan.FromMinutes(15));
        }));

        results.Count(r => r.Allowed).Should().Be(max);
        await using var reader = database.CreateContext();
        (await reader.AuthAttemptCounters.SingleAsync()).Count.Should().Be(max);
    }

    [Fact]
    public async Task TryReserve_AfterWindowEnded_ParallelRequestsStartOneNewWindow()
    {
        const int max = 3;
        await using var database = await PostgresIdentityDatabase.CreateAsync();
        await using (var context = database.CreateContext())
        {
            await new AttemptCountersStorage(context).TryReserve("login:7", max, TimeSpan.FromMinutes(15));
            await context.AuthAttemptCounters.ExecuteUpdateAsync(s =>
                s.SetProperty(x => x.WindowEndsAt, DateTime.UtcNow.AddSeconds(-1)));
        }

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(async _ =>
        {
            await using var context = database.CreateContext();
            return await new AttemptCountersStorage(context).TryReserve("login:7", max, TimeSpan.FromMinutes(15));
        }));

        results.Count(r => r.Allowed).Should().Be(max);
    }
}
