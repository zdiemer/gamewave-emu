using GameWave.Engine;
using GameWave.Graphics;

namespace GameWave.Media;

public enum DeinterlaceMode
{
    /// <summary>Show both fields woven together, as stored.</summary>
    Off,
    /// <summary>Average neighbouring lines of interlaced pictures to hide combing.</summary>
    Blend,
}

/// <summary>
/// The video layer under the OSD: a movie, a still (I-frame) or black. Pictures are
/// converted to RGB once, when they change.
/// </summary>
public sealed class VideoPlane
{
    enum Source
    {
        Black,
        Still,
        Movie,
    }

    readonly object _gate = new();
    readonly uint[] _rgb = new uint[Osd.Width * Osd.Height];
    Source _source = Source.Black;
    Picture? _still;
    Picture? _converted;
    int _convertedVersion = -1;
    int _stillVersion;
    bool _dirty = true;

    public MoviePlayer Movie { get; }

    public DeinterlaceMode Deinterlace { get; set; } = DeinterlaceMode.Blend;

    public VideoPlane(AudioMixer mixer)
    {
        Movie = new MoviePlayer(mixer);
    }

    public void ShowStill(Picture p)
    {
        Movie.Stop(false);
        lock (_gate)
        {
            _still = p;
            _stillVersion++;
            _source = Source.Still;
            _dirty = true;
        }
    }

    public void Clear()
    {
        Movie.Stop(false);
        lock (_gate)
        {
            _still = null;
            _source = Source.Black;
            _dirty = true;
        }
    }

    public void StartMovie()
    {
        lock (_gate)
            _source = Source.Movie;
        Movie.Play();
    }

    public void StopMovie(bool wait) => Movie.Stop(wait);

    public void Render(uint[] frame)
    {
        lock (_gate)
        {
            switch (_source)
            {
                case Source.Black:
                    if (_dirty)
                    {
                        Array.Fill(_rgb, 0xFF000000u);
                        _dirty = false;
                    }
                    break;
                case Source.Still:
                    if (_dirty || _convertedVersion != _stillVersion)
                    {
                        Convert(_still!);
                        _convertedVersion = _stillVersion;
                        _dirty = false;
                    }
                    break;
                case Source.Movie:
                    Movie.WithCurrentPicture(p =>
                    {
                        if (!ReferenceEquals(p, _converted) || _dirty)
                        {
                            Convert(p);
                            _converted = p;
                            _dirty = false;
                        }
                    });
                    break;
            }
            Array.Copy(_rgb, frame, _rgb.Length);
        }
    }

    /// <summary>Forgets the cached conversion (after a settings change).</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _dirty = true;
            _converted = null;
        }
    }

    void Convert(Picture p)
    {
        int w = Math.Min(p.Width, Osd.Width);
        int h = Math.Min(p.Height, Osd.Height);
        bool blend = Deinterlace == DeinterlaceMode.Blend && !p.Progressive;
        int cw = p.CodedWidth / 2;
        for (int y = 0; y < h; y++)
        {
            int yRow = y * p.CodedWidth;
            int yRow2 = Math.Min(y + 1, h - 1) * p.CodedWidth;
            // Interlaced 4:2:0 keeps each field's chroma on that field's lines.
            int chromaLine = p.Progressive ? y >> 1 : ((y >> 2) << 1) | (y & 1);
            int cRow = chromaLine * cw;
            int o = y * Osd.Width;
            for (int x = 0; x < w; x++)
            {
                int luma = p.Y[yRow + x];
                if (blend)
                    luma = (luma + p.Y[yRow2 + x] + 1) >> 1;
                int ci = cRow + (x >> 1);
                _rgb[o + x] = Color.YuvToRgb(luma, p.Cb[ci], p.Cr[ci]);
            }
            for (int x = w; x < Osd.Width; x++)
                _rgb[o + x] = 0xFF000000u;
        }
        for (int y = h; y < Osd.Height; y++)
            Array.Fill(_rgb, 0xFF000000u, y * Osd.Width, Osd.Width);
    }
}
