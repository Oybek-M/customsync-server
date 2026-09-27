namespace CustomSync.Capture.Capture;

public interface IActivityScope
{
    bool ShouldTrackActivity(string peerId, bool isContact);
}

/// <summary>
/// Fail-closed: hech kimning faolligi yozilmaydi.
/// </summary>
public class NoneActivityScope : IActivityScope
{
    public bool ShouldTrackActivity(string peerId, bool isContact) => false;
}
