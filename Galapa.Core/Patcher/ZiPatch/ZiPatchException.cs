namespace Galapa.Core.Patcher.ZiPatch;

public class ZiPatchException(string message = "ZiPatch error", Exception? innerException = null)
    : Exception(message, innerException);
