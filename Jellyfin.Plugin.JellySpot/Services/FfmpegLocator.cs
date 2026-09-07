using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellySpot.Services;

public class FfmpegLocator
{
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IServerConfigurationManager _serverConfig;
    private readonly ILogger<FfmpegLocator> _logger;

    public FfmpegLocator(
        IMediaEncoder mediaEncoder,
        IServerConfigurationManager serverConfig,
        ILogger<FfmpegLocator> logger)
    {
        _mediaEncoder = mediaEncoder;
        _serverConfig = serverConfig;
        _logger = logger;
    }

    public string EncoderPath
    {
        get
        {
            var overridePath = Plugin.Instance?.Configuration.FfmpegPath;
            if (!string.IsNullOrWhiteSpace(overridePath))
            {
                return overridePath.Trim();
            }

            if (!string.IsNullOrWhiteSpace(_mediaEncoder.EncoderPath))
            {
                return _mediaEncoder.EncoderPath;
            }

            try
            {
                var options = _serverConfig.GetConfiguration<EncodingOptions>("encoding");
                if (!string.IsNullOrWhiteSpace(options.EncoderAppPath))
                {
                    return options.EncoderAppPath;
                }

                if (!string.IsNullOrWhiteSpace(options.EncoderAppPathDisplay))
                {
                    return options.EncoderAppPathDisplay;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "JellySpot encoding options probe failed");
            }

            return "ffmpeg";
        }
    }

    public string? EncoderVersion
    {
        get
        {
            try
            {
                var version = _mediaEncoder.EncoderVersion;
                return version is null || version.Major <= 0 ? null : version.ToString();
            }
            catch
            {
                return null;
            }
        }
    }

    public string Source
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Plugin.Instance?.Configuration.FfmpegPath))
            {
                return "override";
            }

            if (!string.IsNullOrWhiteSpace(_mediaEncoder.EncoderPath))
            {
                return "jellyfin";
            }

            return "path";
        }
    }

    public bool IsReady
    {
        get
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(_mediaEncoder.EncoderPath))
                {
                    return true;
                }

                if (_mediaEncoder.EncoderVersion is not null && _mediaEncoder.EncoderVersion.Major > 0)
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "JellySpot FFmpeg IsReady probe failed");
            }

            return !string.IsNullOrWhiteSpace(EncoderPath);
        }
    }
}
