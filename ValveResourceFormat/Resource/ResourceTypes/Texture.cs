using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using K4os.Compression.LZ4;
using SkiaSharp;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.Blocks.ResourceEditInfoStructs;

namespace ValveResourceFormat.ResourceTypes
{
    public class Texture : ResourceData
    {
        private BinaryReader Reader;
        private long DataOffset;
        private Resource Resource;

        public ushort Version { get; private set; }

        public ushort Width { get; private set; }

        public ushort Height { get; private set; }

        public ushort Depth { get; private set; }

        public float[] Reflectivity { get; private set; }

        public VTexFlags Flags { get; private set; }

        public VTexFormat Format { get; private set; }

        public byte NumMipLevels { get; private set; }

        public uint Picmip0Res { get; private set; }

        public Dictionary<VTexExtraData, byte[]> ExtraData { get; private set; }

        public ushort NonPow2Width { get; private set; }

        public ushort NonPow2Height { get; private set; }

        public uint[] CompressedMips { get; private set; }

        public Texture()
        {
            ExtraData = new Dictionary<VTexExtraData, byte[]>();
        }

        public override void Read(BinaryReader reader, Resource resource)
        {
            Reader = reader;
            Resource = resource;

            reader.BaseStream.Position = Offset;

            Version = reader.ReadUInt16();

            if (Version != 1)
            {
                throw new InvalidDataException(string.Format("Unknown vtex version. ({0} != expected 1)", Version));
            }

            Flags = (VTexFlags)reader.ReadUInt16();

            Reflectivity = new[]
            {
                reader.ReadSingle(),
                reader.ReadSingle(),
                reader.ReadSingle(),
                reader.ReadSingle(),
            };
            Width = reader.ReadUInt16();
            Height = reader.ReadUInt16();
            Depth = reader.ReadUInt16();
            NonPow2Width = 0;
            NonPow2Height = 0;
            Format = (VTexFormat)reader.ReadByte();
            NumMipLevels = reader.ReadByte();
            Picmip0Res = reader.ReadUInt32();

            var extraDataOffset = reader.ReadUInt32();
            var extraDataCount = reader.ReadUInt32();

            if (extraDataCount > 0)
            {
                reader.BaseStream.Position += extraDataOffset - 8; // 8 is 2 uint32s we just read

                for (var i = 0; i < extraDataCount; i++)
                {
                    var type = (VTexExtraData)reader.ReadUInt32();
                    var offset = reader.ReadUInt32() - 8;
                    var size = reader.ReadUInt32();

                    var prevOffset = reader.BaseStream.Position;

                    reader.BaseStream.Position += offset;

                    if (type == VTexExtraData.FILL_TO_POWER_OF_TWO)
                    {
                        reader.ReadUInt16();
                        var nw = reader.ReadUInt16();
                        var nh = reader.ReadUInt16();
                        if (nw > 0 && nh > 0 && Width >= nw && Height >= nh)
                        {
                            NonPow2Width = nw;
                            NonPow2Height = nh;
                        }

                        reader.BaseStream.Position -= 6;
                    }

                    ExtraData.Add(type, reader.ReadBytes((int)size));

                    if (type == VTexExtraData.COMPRESSED_MIP_SIZE)
                    {
                        reader.BaseStream.Position -= size;

                        var int1 = reader.ReadUInt32(); // 1?
                        var int2 = reader.ReadUInt32(); // 8?
                        var mips = reader.ReadUInt32();

                        CompressedMips = new uint[mips];

                        for (var mip = 0; mip < mips; mip++)
                        {
                            CompressedMips[mip] = reader.ReadUInt32();
                        }
                    }

                    reader.BaseStream.Position = prevOffset;
                }
            }

            DataOffset = Offset + Size;
        }

        public SKBitmap GenerateBitmap()
        {
            Reader.BaseStream.Position = DataOffset;

            var width = NonPow2Width > 0 ? NonPow2Width : Width;
            var height = NonPow2Height > 0 ? NonPow2Height : Height;

            var imageInfo = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
            Span<byte> data = new byte[imageInfo.RowBytes * imageInfo.Height];

            switch (Format)
            {
                case VTexFormat.DXT1:
                    SkipMipmaps(8);
                    TextureDecompressors.UncompressDXT1(imageInfo, GetDecompressedBuffer(8), data, Width, Height);
                    break;

                case VTexFormat.DXT5:
                    var yCoCg = false;
                    var normalize = false;
                    var invert = false;

                    if (Resource.EditInfo.Structs.ContainsKey(ResourceEditInfo.REDIStruct.SpecialDependencies))
                    {
                        var specialDeps = (SpecialDependencies)Resource.EditInfo.Structs[ResourceEditInfo.REDIStruct.SpecialDependencies];

                        yCoCg = specialDeps.List.Any(dependancy => dependancy.CompilerIdentifier == "CompileTexture" && dependancy.String == "Texture Compiler Version Image YCoCg Conversion");
                        normalize = specialDeps.List.Any(dependancy => dependancy.CompilerIdentifier == "CompileTexture" && dependancy.String == "Texture Compiler Version Image NormalizeNormals");
                        invert = specialDeps.List.Any(dependancy => dependancy.CompilerIdentifier == "CompileTexture" && dependancy.String == "Texture Compiler Version LegacySource1InvertNormals");
                    }

                    SkipMipmaps(16);
                    TextureDecompressors.UncompressDXT5(imageInfo, GetDecompressedBuffer(16), data, Width, Height, yCoCg, normalize, invert);
                    break;

                case VTexFormat.I8:
                    SkipMipmaps(1);

                    return TextureDecompressors.ReadI8(GetDecompressedBuffer(1), Width, Height);

                case VTexFormat.RGBA8888:
                    SkipMipmaps(4);

                    return TextureDecompressors.ReadRGBA8888(GetDecompressedBuffer(4), Width, Height);

                case VTexFormat.R16:
                    SkipMipmaps(2);

                    return TextureDecompressors.ReadR16(GetDecompressedBuffer(2), Width, Height);

                case VTexFormat.RG1616:
                    SkipMipmaps(4);

                    return TextureDecompressors.ReadRG1616(GetDecompressedBuffer(4), Width, Height);

                case VTexFormat.RGBA16161616:
                    SkipMipmaps(8);
                    TextureDecompressors.ReadRGBA16161616(imageInfo, GetDecompressedBuffer(8), data);
                    break;

                case VTexFormat.R16F:
                    SkipMipmaps(2);

                    return TextureDecompressors.ReadR16F(GetDecompressedBuffer(2), Width, Height);

                case VTexFormat.RG1616F:
                    SkipMipmaps(4);

                    return TextureDecompressors.ReadRG1616F(GetDecompressedBuffer(4), Width, Height);

                case VTexFormat.RGBA16161616F:
                    SkipMipmaps(8);
                    TextureDecompressors.ReadRGBA16161616F(imageInfo, GetDecompressedBuffer(8), data);
                    break;

                case VTexFormat.R32F:
                    SkipMipmaps(4);

                    return TextureDecompressors.ReadR32F(GetDecompressedBuffer(4), Width, Height);

                case VTexFormat.RG3232F:
                    SkipMipmaps(8);

                    return TextureDecompressors.ReadRG3232F(GetDecompressedBuffer(8), Width, Height);

                case VTexFormat.RGB323232F:
                    SkipMipmaps(12);

                    return TextureDecompressors.ReadRGB323232F(GetDecompressedBuffer(12), Width, Height);

                case VTexFormat.RGBA32323232F:
                    SkipMipmaps(16);

                    return TextureDecompressors.ReadRGBA32323232F(GetDecompressedBuffer(16), Width, Height);

                case VTexFormat.IA88:
                    SkipMipmaps(2);

                    return TextureDecompressors.ReadIA88(GetDecompressedBuffer(2), Width, Height);

                case VTexFormat.JPG:
                case VTexFormat.PNG2:
                case VTexFormat.PNG:
                    return ReadBuffer();

                default:
                    throw new NotImplementedException(string.Format("Unhandled image type: {0}", Format));
            }

            // pin the managed array so that the GC doesn't move it
            // TODO: There's probably a better way of handling this with Span<byte>
            var gcHandle = GCHandle.Alloc(data.ToArray(), GCHandleType.Pinned);

            // install the pixels with the color type of the pixel data
            var bitmap = new SKBitmap();
            bitmap.InstallPixels(imageInfo, gcHandle.AddrOfPinnedObject(), imageInfo.RowBytes, (address, context) => { gcHandle.Free(); }, null);

            return bitmap;
        }

        private long CalculateBufferSizeForMipLevel(int bytesPerPixel, int mipLevel)
        {
            long size = bytesPerPixel * Width * bytesPerPixel * Height;

            if (Format == VTexFormat.DXT1 || Format == VTexFormat.DXT5)
            {
                var test = Math.Pow(2.0f, mipLevel + 1);
                size = (long)(size / test);
            }

            return size;
        }

        private void SkipMipmaps(int bytesPerPixel)
        {
            if (NumMipLevels < 2)
            {
                return;
            }

            if (CompressedMips != null)
            {
                for (var j = NumMipLevels - 1; j > 0; j--)
                {
                    Reader.BaseStream.Position += CompressedMips[j];
                }

                return;
            }

            for (var j = NumMipLevels; j > 1; j--)
            {
                Reader.BaseStream.Position += CalculateBufferSizeForMipLevel(bytesPerPixel, j);
            }
        }

        private BinaryReader GetDecompressedBuffer(int bytesPerPixel)
        {
            if (CompressedMips == null)
            {
                return Reader;
            }

            var compressedSize = (int)CompressedMips[0];
            var uncompressedSize = CalculateBufferSizeForMipLevel(bytesPerPixel, 0);

            var input = Reader.ReadBytes(compressedSize);
            var output = new Span<byte>(new byte[uncompressedSize]);

            LZ4Codec.Decode(input, output);

            var outStream = new MemoryStream(output.ToArray(), false);

            return new BinaryReader(outStream); // TODO: dispose
        }

        private SKBitmap ReadBuffer()
        {
            return SKBitmap.Decode(Reader.ReadBytes((int)Reader.BaseStream.Length));
        }

        public override string ToString()
        {
            using (var writer = new IndentedTextWriter())
            {
                writer.WriteLine("{0,-12} = {1}", "VTEX Version", Version);
                writer.WriteLine("{0,-12} = {1}", "Width", Width);
                writer.WriteLine("{0,-12} = {1}", "Height", Height);
                writer.WriteLine("{0,-12} = {1}", "Depth", Depth);
                writer.WriteLine("{0,-12} = {1}", "NonPow2W", NonPow2Width);
                writer.WriteLine("{0,-12} = {1}", "NonPow2H", NonPow2Height);
                writer.WriteLine("{0,-12} = ( {1:F6}, {2:F6}, {3:F6}, {4:F6} )", "Reflectivity", Reflectivity[0], Reflectivity[1], Reflectivity[2], Reflectivity[3]);
                writer.WriteLine("{0,-12} = {1}", "NumMipLevels", NumMipLevels);
                writer.WriteLine("{0,-12} = {1}", "Picmip0Res", Picmip0Res);
                writer.WriteLine("{0,-12} = {1} (VTEX_FORMAT_{2})", "Format", (int)Format, Format);
                writer.WriteLine("{0,-12} = 0x{1:X8}", "Flags", (int)Flags);

                foreach (Enum value in Enum.GetValues(Flags.GetType()))
                {
                    if (Flags.HasFlag(value))
                    {
                        writer.WriteLine("{0,-12} | 0x{1:X8} = VTEX_FLAG_{2}", string.Empty, Convert.ToInt32(value), value);
                    }
                }

                writer.WriteLine("{0,-12} = {1} entries:", "Extra Data", ExtraData.Count);

                var entry = 0;

                foreach (var b in ExtraData)
                {
                    writer.WriteLine("{0,-12}   [ Entry {1}: VTEX_EXTRA_DATA_{2} - {3} bytes ]", string.Empty, entry++, b.Key, b.Value.Length);
                }

                return writer.ToString();
            }
        }
    }
}
