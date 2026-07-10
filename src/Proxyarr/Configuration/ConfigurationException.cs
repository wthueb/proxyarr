namespace Proxyarr.Configuration;

/// <summary>Thrown when the YAML configuration is missing, unparsable, or fails validation.</summary>
public sealed class ConfigurationException(string message, Exception? innerException = null)
    : Exception(message, innerException);
