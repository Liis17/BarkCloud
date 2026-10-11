using BarkCloud.Drive.Engine.Tests.Fakes;
using BarkCloud.Proto.Users;

using FluentAssertions;

namespace BarkCloud.Drive.Engine.Tests;

public sealed class UserProfileTests
{
    [Fact]
    public async Task User_loaded_before_clear_is_not_published_after_clear()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invoker = new FakeCallInvoker();
        invoker.Handle<GetUserRequest, GetUserResponse>("/barkcloud.users.UsersApi/GetUser", _ =>
        {
            started.TrySetResult();
            release.Task.GetAwaiter().GetResult();
            return new GetUserResponse { User = new User { Id = 42, Username = "old-user" } };
        });
        var persistedCalls = 0;
        var profile = new UserProfile(new UsersApi.UsersApiClient(invoker), _ =>
            Interlocked.Increment(ref persistedCalls));

        var loading = Task.Factory.StartNew(() => profile.EnsureLoadedAsync().GetAwaiter().GetResult(),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        profile.Clear();
        release.TrySetResult();
        await loading.WaitAsync(TimeSpan.FromSeconds(5));

        profile.UserId.Should().BeNull();
        profile.Username.Should().BeNull();
        profile.AvatarUrl.Should().BeNull();
        persistedCalls.Should().Be(0);
    }

    [Fact]
    public async Task Clearing_profile_after_owner_callback_clears_a_late_persisted_owner()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invoker = new FakeCallInvoker();
        invoker.Handle<GetUserRequest, GetUserResponse>("/barkcloud.users.UsersApi/GetUser",
            _ => new GetUserResponse { User = new User { Id = 42, Username = "old-user" } });
        string? persistedUserId = "previous-user";
        var profile = new UserProfile(new UsersApi.UsersApiClient(invoker), userId =>
        {
            started.TrySetResult();
            release.Task.GetAwaiter().GetResult();
            persistedUserId = userId;
        });

        var loading = Task.Factory.StartNew(() => profile.EnsureLoadedAsync().GetAwaiter().GetResult(),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var clearing = Task.Factory.StartNew(() => profile.Clear(() => persistedUserId = null),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        await Task.Yield();
        release.TrySetResult();
        await loading.WaitAsync(TimeSpan.FromSeconds(5));
        await clearing.WaitAsync(TimeSpan.FromSeconds(5));

        profile.UserId.Should().BeNull();
        persistedUserId.Should().BeNull();
    }
}
