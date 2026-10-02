using BarkCloud.GrpcServer.Tracker;
using BarkCloud.Identity.Services;
using BarkCloud.Shared.Queue.Notifications;

namespace BarkCloud.Identity.Tests.Services;

public class PasswordChangedNotifierTests
{
    [Fact]
    public async Task NotifyAsync_EnqueuesPasswordChangedMailWithDeviceBlock()
    {
        var outbox = new Mock<INotificationOutbox>();
        var sut = new PasswordChangedNotifier(outbox.Object, new RequestContext
        {
            DeviceName = "Pixel", OperationSystem = "Android", IpAddress = "1.1.1.1", AppName = "BarkCloud", AppVersion = "1.0"
        });

        await sut.NotifyAsync(42);

        // Адрес, имя и геолокацию подбирает воркер доставки: хендлер не обращается ни к Users, ни к ip-api.
        outbox.Verify(o => o.EnqueueAsync(
            42,
            NotificationType.PasswordChanged,
            "Пароль успешно изменен",
            It.Is<Dictionary<string, string>>(p =>
                p["ip"] == "1.1.1.1" && p["devicename"] == "Pixel" && p["os"] == "Android"
                && p["appname"] == "BarkCloud v.1.0" && p.ContainsKey("datetime")
                && !p.ContainsKey("username") && !p.ContainsKey("location"))),
            Times.Once);
    }
}
