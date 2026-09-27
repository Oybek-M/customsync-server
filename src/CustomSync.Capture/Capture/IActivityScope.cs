namespace CustomSync.Capture.Capture;

public interface IActivityScope
{
    bool ShouldTrackActivity(string peerId, bool isContact);
}
