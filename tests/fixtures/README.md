`movie.mpg` is an original, two-second test pattern and sine wave, generated with
FFmpeg. It exercises MPEG-2 I/P/B pictures, MPEG Layer II audio, 48 kHz resampling,
end of playback, and looping. It contains no game content.

```sh
ffmpeg -f lavfi -i testsrc2=size=64x48:rate=30000/1001 -f lavfi -i sine=frequency=731:sample_rate=48000 -t 2 -c:v mpeg2video -bf 2 -g 12 -q:v 5 -c:a mp2 -b:a 128k -ac 2 -f mpeg movie.mpg
```
