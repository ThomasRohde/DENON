using System.Text;
using Denon.App.Core.Media;
using Xunit;

namespace Denon.App.Core.Tests;

/// <summary>
/// The audio tag reader (FLAC, MP3, M4A) with small files built in code: the tags, the duration, the cover art,
/// and cut-off or damaged files (null fields, never an exception).
/// </summary>
public sealed class AudioTagReaderTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D, 0x49, 0x48, 0x44, 0x52, 1, 2, 3, 4];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, 0x4A, 0x46, 0x49, 0x46, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9];

    // ---------------------------------------------------------------- FLAC

    private static byte[] FlacFile(bool withId3Prefix = false)
    {
        var file = Concat(
            Ascii("fLaC"),
            FlacBlock(0, last: false, StreamInfo(44100, 44100L * 183)),
            FlacBlock(4, last: false, VorbisComments(
                "TITLE=Ørsted Sang", "artist=Test Artist", "ALBUM=Test Album", "TRACKNUMBER=3/12", "ALBUMARTIST=Other")),
            FlacBlock(1, last: false, new byte[16]), // PADDING
            FlacBlock(6, last: false, FlacPicture(4, "image/jpeg", Jpeg)), // back cover first
            FlacBlock(6, last: true, FlacPicture(3, "image/png", Png)), // front cover
            new byte[4000]); // audio frames
        return withId3Prefix ? Concat(Id3Tag(3, 0, Id3Frame23("TIT2", Latin1Text("From ID3"))), file) : file;
    }

    [Fact]
    public void Flac_ReadsVorbisCommentsStreamInfoAndFrontCover()
    {
        var tags = Read(FlacFile());

        Assert.Equal("Ørsted Sang", tags.Title);
        Assert.Equal("Test Artist", tags.Artist);
        Assert.Equal("Test Album", tags.Album);
        Assert.Equal(3, tags.TrackNumber);
        Assert.Equal(TimeSpan.FromSeconds(183), tags.Duration);
        Assert.NotNull(tags.CoverArt);
        Assert.Equal("image/png", tags.CoverArt.MimeType);
        Assert.Equal(Png, tags.CoverArt.Data);
    }

    [Fact]
    public void Flac_WithoutCoverArt_SkipsThePictures()
    {
        var tags = Read(FlacFile(), includeCoverArt: false);

        Assert.Equal("Ørsted Sang", tags.Title);
        Assert.Null(tags.CoverArt);
    }

    [Fact]
    public void Flac_AfterAnId3Tag_IsStillReadAsFlac()
    {
        var tags = Read(FlacFile(withId3Prefix: true));

        Assert.Equal("From ID3", tags.Title); // the ID3 tag comes first
        Assert.Equal("Test Artist", tags.Artist);
        Assert.Equal(TimeSpan.FromSeconds(183), tags.Duration);
    }

    [Fact]
    public void Flac_CutInsideTheFrontCover_KeepsTheTextAndTheCompleteBackCover()
    {
        var file = FlacFile();
        var tags = Read(file[..(IndexOf(file, Png) + 5)]);

        Assert.Equal("Ørsted Sang", tags.Title);
        Assert.Equal(TimeSpan.FromSeconds(183), tags.Duration);
        Assert.NotNull(tags.CoverArt);
        Assert.Equal("image/jpeg", tags.CoverArt.MimeType);
    }

    [Fact]
    public void Flac_CutInsideTheVorbisComments_KeepsTheDuration()
    {
        var file = FlacFile();
        var tags = Read(file[..(IndexOf(file, Ascii("artist=")) + 3)]);

        Assert.Equal(TimeSpan.FromSeconds(183), tags.Duration);
        Assert.Equal("Ørsted Sang", tags.Title); // the comments before the cut stay
        Assert.Null(tags.Artist);
        Assert.Null(tags.CoverArt);
    }

    [Fact]
    public void Flac_CutInsideStreamInfo_GivesNoFields()
    {
        var tags = Read(FlacFile()[..12]);

        Assert.Equal(AudioTags.Empty, tags);
    }

    // ---------------------------------------------------------------- MP3

    // MPEG-1 Layer III, 128 kbit/s, 44.1 kHz, joint stereo, no CRC: each frame is 417 bytes.
    private static readonly byte[] MpegHeader = [0xFF, 0xFB, 0x90, 0x40];
    private const int MpegFrameLength = 417;

    private static byte[] Mp3V23File() => Concat(
        Id3Tag(3, 0, Concat(
            Id3Frame23("TIT2", Latin1Text("Latin Title")),
            Id3Frame23("TPE1", Utf16Text("Ünicode Artist")),
            Id3Frame23("TALB", Latin1Text("V23 Album")),
            Id3Frame23("TRCK", Latin1Text("7/10")),
            Id3Frame23("APIC", Concat([0], Ascii("image/jpeg"), [0, 3], Ascii("cover"), [0], Jpeg)),
            new byte[64])), // padding
        XingFrame(2000),
        MpegFrame(),
        MpegFrame());

    private static byte[] Mp3V24File() => Concat(
        Id3Tag(4, 0, Concat(
            Id3Frame24("TIT2", Utf8Text("First\0Second")),
            Id3Frame24("TPE1", Utf8Text("V24 Artist")),
            // unsynchronised with a data length indicator: "Aÿà" is 41 FF E0, stored as 41 FF 00 E0
            Id3Frame24("TALB", Concat(Syncsafe(4), [0, 0x41, 0xFF, 0x00, 0xE0]), formatFlags: 0x03),
            Id3Frame24("TRCK", Latin1Text("5")),
            Id3Frame24("APIC", Concat([1], Ascii("image/png"), [0, 3, 0xFF, 0xFE], Encoding.Unicode.GetBytes("Front"), [0, 0], Png)))),
        VbriFrame(1500),
        MpegFrame(),
        MpegFrame());

    [Fact]
    public void Mp3V23_ReadsTextFramesApicAndXingDuration()
    {
        var tags = Read(Mp3V23File());

        Assert.Equal("Latin Title", tags.Title);
        Assert.Equal("Ünicode Artist", tags.Artist);
        Assert.Equal("V23 Album", tags.Album);
        Assert.Equal(7, tags.TrackNumber);
        Assert.Equal(2000 * 1152 / 44100.0, tags.Duration!.Value.TotalSeconds, precision: 3);
        Assert.NotNull(tags.CoverArt);
        Assert.Equal("image/jpeg", tags.CoverArt.MimeType);
        Assert.Equal(Jpeg, tags.CoverArt.Data);
    }

    [Fact]
    public void Mp3V24_ReadsUtf8UnsynchronisedFramesAndVbriDuration()
    {
        var tags = Read(Mp3V24File());

        Assert.Equal("First", tags.Title);
        Assert.Equal("V24 Artist", tags.Artist);
        Assert.Equal("Aÿà", tags.Album);
        Assert.Equal(5, tags.TrackNumber);
        Assert.Equal(1500 * 1152 / 44100.0, tags.Duration!.Value.TotalSeconds, precision: 3);
        Assert.NotNull(tags.CoverArt);
        Assert.Equal("image/png", tags.CoverArt.MimeType);
        Assert.Equal(Png, tags.CoverArt.Data);
    }

    [Fact]
    public void Mp3V23_WholeTagUnsynchronisation_IsRemoved()
    {
        var body = Concat(Id3Frame23("TIT2", Latin1Text("ÿà")), Id3Frame23("TPE1", Latin1Text("After")));
        var tags = Read(Concat(Id3Tag(3, 0x80, Unsynchronise(body)), MpegFrame(), MpegFrame()));

        Assert.Equal("ÿà", tags.Title);
        Assert.Equal("After", tags.Artist);
    }

    [Fact]
    public void Mp3V22_ReadsThreeLetterFramesAndPic()
    {
        var tags = Read(Concat(
            Id3Tag(2, 0, Concat(
                Id3Frame22("TT2", Latin1Text("Old Title")),
                Id3Frame22("TP1", Latin1Text("Old Artist")),
                Id3Frame22("TRK", Latin1Text("9")),
                Id3Frame22("PIC", Concat([0], Ascii("PNG"), [3, 0], Png)))),
            XingFrame(100),
            MpegFrame()));

        Assert.Equal("Old Title", tags.Title);
        Assert.Equal("Old Artist", tags.Artist);
        Assert.Equal(9, tags.TrackNumber);
        Assert.Equal("image/png", tags.CoverArt?.MimeType);
        Assert.Equal(100 * 1152 / 44100.0, tags.Duration!.Value.TotalSeconds, precision: 3);
    }

    [Fact]
    public void Mp3_WithoutVbrHeader_UsesTheBitrateAndReadsId3v1()
    {
        // 16000 bytes of 128 kbit/s audio is 1 second.
        var audio = new byte[16000];
        for (var offset = 0; offset < audio.Length; offset += MpegFrameLength)
        {
            MpegHeader.AsSpan(0, Math.Min(4, audio.Length - offset)).CopyTo(audio.AsSpan(offset));
        }

        var tags = Read(Concat(audio, Id3v1("V1 Title", "V1 Artist", "V1 Album", track: 4)));

        Assert.Equal("V1 Title", tags.Title);
        Assert.Equal("V1 Artist", tags.Artist);
        Assert.Equal("V1 Album", tags.Album);
        Assert.Equal(4, tags.TrackNumber);
        Assert.Equal(1.0, tags.Duration!.Value.TotalSeconds, precision: 6);
        Assert.Null(tags.CoverArt);
    }

    [Fact]
    public void Mp3_Id3v1_FillsOnlyFieldsThatId3v2DoesNotHave()
    {
        var tags = Read(Concat(
            Id3Tag(3, 0, Id3Frame23("TIT2", Latin1Text("V2 Title"))),
            MpegFrame(),
            MpegFrame(),
            Id3v1("V1 Title", "V1 Artist", "", track: 2)));

        Assert.Equal("V2 Title", tags.Title);
        Assert.Equal("V1 Artist", tags.Artist);
        Assert.Null(tags.Album);
        Assert.Equal(2, tags.TrackNumber);
    }

    [Fact]
    public void Mp3_CutInsideTheApicFrame_KeepsTheFramesBeforeIt()
    {
        var file = Mp3V23File();
        var tags = Read(file[..(IndexOf(file, Jpeg) + 4)]);

        Assert.Equal("Latin Title", tags.Title);
        Assert.Equal("Ünicode Artist", tags.Artist);
        Assert.Equal(7, tags.TrackNumber);
        Assert.Null(tags.CoverArt);
        Assert.Null(tags.Duration);
    }

    [Fact]
    public void Mp3_CutInsideTheTagHeader_GivesNoFields()
    {
        Assert.Equal(AudioTags.Empty, Read(Mp3V23File()[..7]));
    }

    [Fact]
    public void Mp3_UnknownBytesWithMp3Extension_GivesNoFields()
    {
        var noise = new byte[3000];
        new Random(7).NextBytes(noise);
        var noSync = noise.Select(b => (byte)(b & 0x7F)).ToArray(); // no 0xFF byte, so no frame sync

        var tags = AudioTagReader.ReadUnguarded(new MemoryStream(noSync), extension: ".mp3");

        Assert.Equal(AudioTags.Empty, tags);
    }

    // ---------------------------------------------------------------- M4A

    private static byte[] M4aFile(bool moovAtEnd = false, bool fullBoxMeta = true, bool version1Header = false)
    {
        var mvhd = version1Header
            ? Box("mvhd", [1, 0, 0, 0], new byte[16], Be32(44100), Be64(44100UL * 200), new byte[80])
            : Box("mvhd", [0, 0, 0, 0], Be32(0), Be32(0), Be32(1000), Be32(185_500), new byte[80]);
        var hdlr = Box("hdlr", Be32(0), Be32(0), Ascii("mdir"), Ascii("appl"), Be32(0), Be32(0), [0]);
        var ilst = Box("ilst",
            Box("©too", DataBox(1, Utf8("Encoder"))),
            Box("©nam", DataBox(1, Utf8("Mp4 Titel æøå"))),
            Box("aART", DataBox(1, Utf8("Album Artist"))),
            Box("©alb", DataBox(1, Utf8("Mp4 Album"))),
            Box("trkn", DataBox(0, [0, 0, 0, 4, 0, 10, 0, 0])),
            Box("covr", DataBox(13, Jpeg)));
        var meta = fullBoxMeta ? Box("meta", Be32(0), hdlr, ilst) : Box("meta", hdlr, ilst);
        var moov = Box("moov", mvhd, Box("trak", new byte[40]), Box("udta", meta));
        var ftyp = Box("ftyp", Ascii("M4A "), Be32(0), Ascii("M4A isomiso2"));
        if (!moovAtEnd)
        {
            return Concat(ftyp, moov, Box("mdat", new byte[2000]));
        }

        // A 64-bit "largesize" mdat before the moov box.
        var largeMdat = Concat(Be32(1), Ascii("mdat"), Be64(16 + 2000), new byte[2000]);
        return Concat(ftyp, largeMdat, moov);
    }

    [Fact]
    public void M4a_ReadsIlstItemsMvhdAndCover()
    {
        var tags = Read(M4aFile());

        Assert.Equal("Mp4 Titel æøå", tags.Title);
        Assert.Equal("Album Artist", tags.Artist); // no ©ART: the album artist
        Assert.Equal("Mp4 Album", tags.Album);
        Assert.Equal(4, tags.TrackNumber);
        Assert.Equal(TimeSpan.FromSeconds(185.5), tags.Duration);
        Assert.NotNull(tags.CoverArt);
        Assert.Equal("image/jpeg", tags.CoverArt.MimeType);
        Assert.Equal(Jpeg, tags.CoverArt.Data);
    }

    [Fact]
    public void M4a_MoovAfterLargeMdat_IsFoundBySeeking()
    {
        var tags = Read(M4aFile(moovAtEnd: true, version1Header: true));

        Assert.Equal("Mp4 Titel æøå", tags.Title);
        Assert.Equal(TimeSpan.FromSeconds(200), tags.Duration);
    }

    [Fact]
    public void M4a_QuickTimeMetaWithoutVersionField_IsRead()
    {
        var tags = Read(M4aFile(fullBoxMeta: false), includeCoverArt: false);

        Assert.Equal("Mp4 Album", tags.Album);
        Assert.Null(tags.CoverArt);
    }

    [Fact]
    public void M4a_CutInsideTheCover_KeepsTheTextAndDuration()
    {
        var file = M4aFile();
        var tags = Read(file[..(IndexOf(file, Jpeg) + 6)]);

        Assert.Equal("Mp4 Titel æøå", tags.Title);
        Assert.Equal(4, tags.TrackNumber);
        Assert.Equal(TimeSpan.FromSeconds(185.5), tags.Duration);
        Assert.Null(tags.CoverArt);
    }

    [Fact]
    public void M4a_CutInsideMvhd_GivesNoFields()
    {
        var file = M4aFile();
        Assert.Equal(AudioTags.Empty, Read(file[..(IndexOf(file, Ascii("mvhd")) + 10)]));
    }

    // ---------------------------------------------------------------- every format

    public static TheoryData<string> Fixtures => ["flac", "flac-id3", "mp3-v23", "mp3-v24", "m4a", "m4a-end"];

    private static byte[] Fixture(string name) => name switch
    {
        "flac" => FlacFile(),
        "flac-id3" => FlacFile(withId3Prefix: true),
        "mp3-v23" => Mp3V23File(),
        "mp3-v24" => Mp3V24File(),
        "m4a" => M4aFile(),
        _ => M4aFile(moovAtEnd: true, version1Header: true),
    };

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void EveryCutOffLength_ParsesWithoutAnException(string name)
    {
        var file = Fixture(name);
        for (var length = 0; length <= file.Length; length++)
        {
            AudioTagReader.ReadUnguarded(new MemoryStream(file, 0, length));
        }
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void DamagedBytes_ParseWithoutAnException(string name)
    {
        var original = Fixture(name);
        var random = new Random(name.Length * 7919);
        for (var round = 0; round < 500; round++)
        {
            var file = (byte[])original.Clone();
            var damage = random.Next(1, 9);
            for (var i = 0; i < damage; i++)
            {
                // Damage the header region most of the time: that is where the parsers work.
                var limit = random.Next(4) == 0 ? file.Length : Math.Min(file.Length, 400);
                file[random.Next(limit)] = (byte)random.Next(256);
            }

            AudioTagReader.ReadUnguarded(new MemoryStream(file));
        }
    }

    [Fact]
    public void UnknownFormat_GivesNoFields()
    {
        Assert.Equal(AudioTags.Empty, AudioTagReader.Read(new MemoryStream(Ascii("OggS and some more bytes")), extension: ".ogg"));
        Assert.Equal(AudioTags.Empty, Read([]));
    }

    [Fact]
    public void Path_ReadsTheFileAndReleasesIt()
    {
        var path = Path.Combine(Path.GetTempPath(), "denon-tag-test-" + Guid.NewGuid().ToString("N") + ".flac");
        File.WriteAllBytes(path, FlacFile());
        try
        {
            Assert.Equal("Ørsted Sang", AudioTagReader.Read(path).Title);
        }
        finally
        {
            File.Delete(path); // throws if the reader kept the file open
        }
    }

    [Fact]
    public void Path_MissingOrEmpty_GivesNoFields()
    {
        Assert.Equal(AudioTags.Empty, AudioTagReader.Read(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".mp3")));
        Assert.Equal(AudioTags.Empty, AudioTagReader.Read(""));
    }

    // ---------------------------------------------------------------- fixture builders

    private static AudioTags Read(byte[] file, bool includeCoverArt = true) =>
        AudioTagReader.ReadUnguarded(new MemoryStream(file), includeCoverArt);

    private static byte[] FlacBlock(int type, bool last, byte[] data) =>
        Concat([(byte)((last ? 0x80 : 0) | type), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length], data);

    private static byte[] StreamInfo(int sampleRate, long totalSamples)
    {
        var info = new byte[34];
        info[10] = (byte)(sampleRate >> 12);
        info[11] = (byte)(sampleRate >> 4);
        info[12] = (byte)(((sampleRate & 0xF) << 4) | (1 << 1)); // 2 channels, high bit of (16 - 1) bits per sample
        info[13] = (byte)(0xF0 | (int)((totalSamples >> 32) & 0xF));
        Be32((uint)totalSamples).CopyTo(info, 14);
        return info;
    }

    private static byte[] VorbisComments(params string[] comments)
    {
        var vendor = Utf8("reference libFLAC 1.4.3");
        var parts = new List<byte[]> { Le32(vendor.Length), vendor, Le32(comments.Length) };
        foreach (var comment in comments)
        {
            var bytes = Utf8(comment);
            parts.Add(Le32(bytes.Length));
            parts.Add(bytes);
        }

        return Concat([.. parts]);
    }

    private static byte[] FlacPicture(int pictureType, string mimeType, byte[] image)
    {
        var description = Utf8("cover");
        return Concat(
            Be32((uint)pictureType), Be32((uint)mimeType.Length), Ascii(mimeType),
            Be32((uint)description.Length), description, new byte[16], Be32((uint)image.Length), image);
    }

    private static byte[] Id3Tag(int major, byte flags, byte[] body) =>
        Concat(Ascii("ID3"), [(byte)major, 0, flags], Syncsafe(body.Length), body);

    private static byte[] Id3Frame22(string id, byte[] data) =>
        Concat(Ascii(id), [(byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length], data);

    private static byte[] Id3Frame23(string id, byte[] data) => Concat(Ascii(id), Be32((uint)data.Length), [0, 0], data);

    private static byte[] Id3Frame24(string id, byte[] data, byte formatFlags = 0) =>
        Concat(Ascii(id), Syncsafe(data.Length), [0, formatFlags], data);

    private static byte[] Latin1Text(string text) => Concat([0], Encoding.Latin1.GetBytes(text));

    private static byte[] Utf16Text(string text) => Concat([1, 0xFF, 0xFE], Encoding.Unicode.GetBytes(text), [0, 0]);

    private static byte[] Utf8Text(string text) => Concat([3], Utf8(text));

    private static byte[] Syncsafe(int value) =>
        [(byte)((value >> 21) & 0x7F), (byte)((value >> 14) & 0x7F), (byte)((value >> 7) & 0x7F), (byte)(value & 0x7F)];

    private static byte[] Unsynchronise(byte[] data)
    {
        var result = new List<byte>();
        for (var i = 0; i < data.Length; i++)
        {
            result.Add(data[i]);
            if (data[i] == 0xFF && (i + 1 == data.Length || data[i + 1] == 0 || data[i + 1] >= 0xE0))
            {
                result.Add(0);
            }
        }

        return [.. result];
    }

    private static byte[] Id3v1(string title, string artist, string album, byte track)
    {
        var tag = new byte[128];
        Ascii("TAG").CopyTo(tag, 0);
        Encoding.Latin1.GetBytes(title).CopyTo(tag, 3);
        Encoding.Latin1.GetBytes(artist).CopyTo(tag, 33);
        Encoding.Latin1.GetBytes(album).CopyTo(tag, 63);
        tag[125] = 0;
        tag[126] = track;
        tag[127] = 255;
        return tag;
    }

    private static byte[] MpegFrame(byte[]? atOffset36 = null)
    {
        var frame = new byte[MpegFrameLength];
        MpegHeader.CopyTo(frame, 0);
        atOffset36?.CopyTo(frame, 36);
        return frame;
    }

    private static byte[] XingFrame(uint frames) => MpegFrame(Concat(Ascii("Xing"), Be32(1), Be32(frames)));

    private static byte[] VbriFrame(uint frames) =>
        MpegFrame(Concat(Ascii("VBRI"), [0, 1, 0, 0, 0, 50], Be32(123456), Be32(frames)));

    private static byte[] Box(string type, params byte[][] content)
    {
        var body = Concat(content);
        return Concat(Be32((uint)(8 + body.Length)), Encoding.Latin1.GetBytes(type), body);
    }

    private static byte[] DataBox(int dataType, byte[] value) => Box("data", Be32((uint)dataType), Be32(0), value);

    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private static byte[] Be32(uint value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    private static byte[] Be32(int value) => Be32((uint)value);

    private static byte[] Be64(ulong value) => Concat(Be32((uint)(value >> 32)), Be32((uint)value));

    private static byte[] Le32(int value) => [(byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24)];

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(part => part).ToArray();

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        var index = haystack.AsSpan().IndexOf(needle);
        Assert.True(index >= 0, "fixture part not found");
        return index;
    }
}
