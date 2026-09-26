namespace CustomSync.Capture.Capture;

public class MessageCacheException(string message, Exception? inner = null) : Exception(message, inner);
