namespace PeerDesk.Core;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public class TestClock : IClock
{
    public DateTimeOffset CurrentTime { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UtcNow => CurrentTime;
    public void Advance(TimeSpan span) => CurrentTime = CurrentTime.Add(span);
}
