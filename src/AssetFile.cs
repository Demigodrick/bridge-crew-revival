using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace StbcRevival
{
    /// <summary>
    /// Reads objects straight out of one of the game's own asset files (stbc_Data/sharedassets*.assets), on the
    /// player's PC, without loading the scene that owns them. Only what this game needs: Unity 5.4.4p1's serialized
    /// file format (version 15). These files carry no type information, so each object type's layout is written out
    /// here (Mesh so far), as Unity 5.4 serializes it. Plain .NET 3.5: the mod and the offline tool share this file.
    /// </summary>
    internal sealed class AssetFile : IDisposable
    {
        internal sealed class Entry
        {
            public long PathId;
            public long Offset; // from the start of the file
            public uint Size;
            public int ClassId;
        }

        internal const int MeshClass = 43;

        public readonly string Path;
        public readonly string UnityVersion;
        public readonly List<Entry> Objects = new List<Entry>();
        private readonly FileStream stream;
        private readonly BinaryReader reader;

        public AssetFile(string path)
        {
            Path = path;
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            reader = new BinaryReader(stream);
            // Header, big-endian: metadata size, file size, format version, data offset, endianness.
            reader.ReadBytes(4);
            reader.ReadBytes(4);
            uint version = BigEndian(reader.ReadBytes(4));
            uint dataOffset = BigEndian(reader.ReadBytes(4));
            bool bigEndian = reader.ReadByte() != 0;
            reader.ReadBytes(3);
            if (version != 15 || bigEndian) throw new NotSupportedException($"{path}: format version {version}{(bigEndian ? ", big-endian" : "")}; only Unity 5.4's version 15 is read");
            UnityVersion = ReadCString();
            reader.ReadInt32(); // platform
            if (reader.ReadByte() != 0) throw new NotSupportedException($"{path}: has type trees; this reader only knows the layouts without them");
            int types = reader.ReadInt32();
            for (int i = 0; i < types; i++)
            {
                int classId = reader.ReadInt32();
                if (classId < 0) reader.ReadBytes(16); // a script's type: its script hash
                reader.ReadBytes(16);                  // the type's hash
            }
            int count = reader.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                Align(4);
                var entry = new Entry { PathId = reader.ReadInt64() };
                entry.Offset = dataOffset + reader.ReadUInt32();
                entry.Size = reader.ReadUInt32();
                reader.ReadInt32();                    // type id
                entry.ClassId = reader.ReadUInt16();
                reader.ReadInt16();                    // script type index
                reader.ReadByte();                     // stripped
                Objects.Add(entry);
                byId[entry.PathId] = entry;
            }
            int scripts = reader.ReadInt32();
            for (int i = 0; i < scripts; i++)
            {
                reader.ReadInt32();
                Align(4);
                reader.ReadInt64();
            }
            // The other files this one's links (PPtr file IDs 1, 2, ...) point into.
            int externals = reader.ReadInt32();
            for (int i = 0; i < externals; i++)
            {
                ReadCString();         // (empty)
                reader.ReadBytes(16);  // GUID
                reader.ReadInt32();    // type
                Externals.Add(ReadCString());
            }
        }

        public readonly List<string> Externals = new List<string>();
        private readonly Dictionary<long, Entry> byId = new Dictionary<long, Entry>();

        public Entry Find(long pathId) => byId.TryGetValue(pathId, out var entry) ? entry : null;

        /// <summary>The file a link's file ID means: null for this one, else the external's file name.</summary>
        public string FileOf(PPtr link) => link.FileId == 0 ? null : link.FileId <= Externals.Count ? System.IO.Path.GetFileName(Externals[link.FileId - 1]) : "?";

        public void Dispose() => stream.Dispose();

        /// <summary>A reader at the start of an object's data.</summary>
        public BinaryReader At(Entry entry)
        {
            stream.Position = entry.Offset;
            return reader;
        }

        /// <summary>The object's name, for the types whose data starts with one (Mesh, Texture2D, Material...).</summary>
        public string NameOf(Entry entry)
        {
            At(entry);
            int length = reader.ReadInt32();
            if (length < 0 || length > 1024 || length > entry.Size - 4) return null;
            return Encoding.UTF8.GetString(reader.ReadBytes(length));
        }

        // ---- Reading helpers (the object data is little-endian, fields aligned to 4 after odd sizes) -----------

        internal void Align(int to)
        {
            long misaligned = stream.Position % to;
            if (misaligned != 0) stream.Position += to - misaligned;
        }

        // Within an object, alignment counts from the object's start: objects start on 8 bytes in these files.
        internal static void Align(BinaryReader r) => r.BaseStream.Position = (r.BaseStream.Position + 3) & ~3L;

        internal static string ReadString(BinaryReader r)
        {
            int length = r.ReadInt32();
            var text = Encoding.UTF8.GetString(r.ReadBytes(length));
            Align(r);
            return text;
        }

        internal static PPtr ReadPPtr(BinaryReader r) => new PPtr { FileId = r.ReadInt32(), PathId = r.ReadInt64() };

        internal static List<PPtr> ReadPPtrs(BinaryReader r)
        {
            var links = new List<PPtr>();
            for (int n = r.ReadInt32(), i = 0; i < n; i++) links.Add(ReadPPtr(r));
            return links;
        }

        internal static byte[] ReadBytes(BinaryReader r)
        {
            var bytes = r.ReadBytes(r.ReadInt32());
            Align(r);
            return bytes;
        }

        private string ReadCString()
        {
            var bytes = new List<byte>();
            for (byte b; (b = reader.ReadByte()) != 0;) bytes.Add(b);
            return Encoding.ASCII.GetString(bytes.ToArray());
        }

        private static uint BigEndian(byte[] b) => (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
    }

    /// <summary>A link to an object: in this file (file ID 0) or in one of its externals.</summary>
    internal struct PPtr
    {
        public int FileId;
        public long PathId;
        public bool IsNull => FileId == 0 && PathId == 0;
        public override string ToString() => $"{FileId}:{PathId}";
    }

    /// <summary>A GameObject as Unity 5.4 serializes it (class 1): its components and name.</summary>
    internal sealed class GameObjectAsset
    {
        public readonly List<KeyValuePair<int, PPtr>> Components = new List<KeyValuePair<int, PPtr>>(); // class ID, component
        public string Name;

        public static GameObjectAsset Read(BinaryReader r)
        {
            var g = new GameObjectAsset();
            for (int n = r.ReadInt32(), i = 0; i < n; i++) g.Components.Add(new KeyValuePair<int, PPtr>(r.ReadInt32(), AssetFile.ReadPPtr(r)));
            r.ReadUInt32(); // layer
            g.Name = AssetFile.ReadString(r);
            return g;
        }

        public PPtr Component(int classId)
        {
            foreach (var c in Components) if (c.Key == classId) return c.Value;
            return default(PPtr);
        }
    }

    /// <summary>A Transform as Unity 5.4 serializes it (class 4).</summary>
    internal sealed class TransformAsset
    {
        public PPtr GameObject, Father;
        public float[] Rotation, Position, Scale; // x y z w; x y z; x y z
        public List<PPtr> Children;

        public static TransformAsset Read(BinaryReader r)
        {
            var t = new TransformAsset { GameObject = AssetFile.ReadPPtr(r) };
            t.Rotation = new[] { r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle() };
            t.Position = new[] { r.ReadSingle(), r.ReadSingle(), r.ReadSingle() };
            t.Scale = new[] { r.ReadSingle(), r.ReadSingle(), r.ReadSingle() };
            t.Children = AssetFile.ReadPPtrs(r);
            t.Father = AssetFile.ReadPPtr(r);
            return t;
        }
    }

    /// <summary>A Material as Unity 5.4 serializes it (class 21): its shader, keywords, textures and values.</summary>
    internal sealed class MaterialAsset
    {
        internal sealed class TexEnv
        {
            public string Name;
            public PPtr Texture;
            public float[] Scale, Offset; // 2 each
        }

        public string Name, Keywords;
        public PPtr Shader;
        public uint LightmapFlags;
        public int RenderQueue;
        public readonly List<KeyValuePair<string, string>> Tags = new List<KeyValuePair<string, string>>();
        public readonly List<TexEnv> Textures = new List<TexEnv>();
        public readonly List<KeyValuePair<string, float>> Floats = new List<KeyValuePair<string, float>>();
        public readonly List<KeyValuePair<string, float[]>> Colors = new List<KeyValuePair<string, float[]>>();

        public static MaterialAsset Read(BinaryReader r)
        {
            var m = new MaterialAsset { Name = AssetFile.ReadString(r), Shader = AssetFile.ReadPPtr(r), Keywords = AssetFile.ReadString(r) };
            m.LightmapFlags = r.ReadUInt32();
            m.RenderQueue = r.ReadInt32();
            for (int n = r.ReadInt32(), i = 0; i < n; i++) m.Tags.Add(new KeyValuePair<string, string>(AssetFile.ReadString(r), AssetFile.ReadString(r)));
            for (int n = r.ReadInt32(), i = 0; i < n; i++)
                m.Textures.Add(new TexEnv { Name = AssetFile.ReadString(r), Texture = AssetFile.ReadPPtr(r), Scale = new[] { r.ReadSingle(), r.ReadSingle() }, Offset = new[] { r.ReadSingle(), r.ReadSingle() } });
            for (int n = r.ReadInt32(), i = 0; i < n; i++) m.Floats.Add(new KeyValuePair<string, float>(AssetFile.ReadString(r), r.ReadSingle()));
            for (int n = r.ReadInt32(), i = 0; i < n; i++)
                m.Colors.Add(new KeyValuePair<string, float[]>(AssetFile.ReadString(r), new[] { r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle() }));
            return m;
        }
    }

    /// <summary>A Texture2D as Unity 5.4 serializes it (class 28): its format and where its pixels are.</summary>
    internal sealed class TextureAsset
    {
        public string Name, StreamPath;
        public int Width, Height, ImageSize, Format, MipCount, FilterMode, Aniso, WrapMode, ColorSpace;
        public float MipBias;
        public byte[] Data;       // the pixels, when they're in the asset file itself
        public uint StreamOffset, StreamSize; // else in the .resS file beside it

        public static TextureAsset Read(BinaryReader r)
        {
            var t = new TextureAsset { Name = AssetFile.ReadString(r), Width = r.ReadInt32(), Height = r.ReadInt32(), ImageSize = r.ReadInt32() };
            t.Format = r.ReadInt32();
            t.MipCount = r.ReadInt32();
            r.ReadBoolean(); // readable
            r.ReadBoolean(); // read allowed
            AssetFile.Align(r);
            r.ReadInt32();   // image count
            r.ReadInt32();   // dimension
            t.FilterMode = r.ReadInt32();
            t.Aniso = r.ReadInt32();
            t.MipBias = r.ReadSingle();
            t.WrapMode = r.ReadInt32();
            r.ReadInt32();   // lightmap format
            t.ColorSpace = r.ReadInt32();
            t.Data = AssetFile.ReadBytes(r);
            t.StreamOffset = r.ReadUInt32();
            t.StreamSize = r.ReadUInt32();
            t.StreamPath = AssetFile.ReadString(r);
            return t;
        }
    }

    /// <summary>A SkinnedMeshRenderer as Unity 5.4 serializes it (class 137): its materials and mesh.</summary>
    internal sealed class SkinnedRendererAsset
    {
        public PPtr GameObject, Mesh, RootBone;
        public List<PPtr> Materials, Bones;
        public float[] BlendShapeWeights;

        public static SkinnedRendererAsset Read(BinaryReader r)
        {
            var s = new SkinnedRendererAsset { GameObject = AssetFile.ReadPPtr(r) };
            r.ReadBytes(6);   // enabled, cast shadows, receive shadows, motion vectors, light probes, reflection probes
            AssetFile.Align(r);
            r.ReadBytes(4);   // lightmap indices (static, dynamic)
            r.ReadBytes(32);  // lightmap tiling and offset (static, dynamic)
            s.Materials = AssetFile.ReadPPtrs(r);
            for (int n = r.ReadInt32(), i = 0; i < n; i++) r.ReadUInt32(); // subset indices
            AssetFile.ReadPPtr(r); // static batch root
            AssetFile.ReadPPtr(r); // probe anchor
            AssetFile.ReadPPtr(r); // light probe volume override
            r.ReadInt32();         // sorting layer ID
            r.ReadInt16();         // sorting order
            AssetFile.Align(r);
            r.ReadInt32();         // quality
            r.ReadBoolean();       // update when offscreen
            r.ReadBoolean();       // skinned motion vectors
            AssetFile.Align(r);
            s.Mesh = AssetFile.ReadPPtr(r);
            s.Bones = AssetFile.ReadPPtrs(r);
            int weights = r.ReadInt32();
            s.BlendShapeWeights = new float[weights];
            for (int i = 0; i < weights; i++) s.BlendShapeWeights[i] = r.ReadSingle();
            s.RootBone = AssetFile.ReadPPtr(r);
            return s;
        }
    }

    /// <summary>A MeshFilter (class 33): which mesh a GameObject draws.</summary>
    internal sealed class MeshFilterAsset
    {
        public PPtr GameObject, Mesh;

        public static MeshFilterAsset Read(BinaryReader r) => new MeshFilterAsset { GameObject = AssetFile.ReadPPtr(r), Mesh = AssetFile.ReadPPtr(r) };
    }

    /// <summary>A MeshRenderer (class 23) as Unity 5.4 serializes it: on or off, its materials, and for a
    /// statically batched one, the sub-meshes of the combined mesh it draws.</summary>
    internal sealed class MeshRendererAsset
    {
        public PPtr GameObject, StaticBatchRoot;
        public bool Enabled;
        public List<PPtr> Materials;
        public List<uint> SubsetIndices;

        public static MeshRendererAsset Read(BinaryReader r)
        {
            var m = new MeshRendererAsset { GameObject = AssetFile.ReadPPtr(r) };
            m.Enabled = r.ReadBoolean();
            r.ReadBytes(5);   // cast shadows, receive shadows, motion vectors, light probes, reflection probes
            AssetFile.Align(r);
            r.ReadBytes(4);   // lightmap indices (static, dynamic)
            r.ReadBytes(32);  // lightmap tiling and offset (static, dynamic)
            m.Materials = AssetFile.ReadPPtrs(r);
            m.SubsetIndices = new List<uint>();
            for (int n = r.ReadInt32(), i = 0; i < n; i++) m.SubsetIndices.Add(r.ReadUInt32());
            m.StaticBatchRoot = AssetFile.ReadPPtr(r);
            return m;
        }
    }

    /// <summary>A Mesh as Unity 5.4 serializes it (class 43), the parts a skinned, blend-shaped head needs.</summary>
    internal sealed class MeshAsset
    {
        internal sealed class SubMesh
        {
            public uint FirstByte, IndexCount, FirstVertex, VertexCount;
            public int Topology;
        }

        internal sealed class ShapeVertex
        {
            public float[] Vertex, Normal, Tangent; // 3 each
            public uint Index;
        }

        internal sealed class Shape
        {
            public uint FirstVertex, VertexCount;
            public bool HasNormals, HasTangents;
        }

        internal sealed class ShapeChannel
        {
            public string Name;
            public uint NameHash;
            public int FrameIndex, FrameCount;
        }

        internal sealed class Channel
        {
            public byte Stream, Offset, Format, Dimension;
        }

        public string Name;
        public readonly List<SubMesh> SubMeshes = new List<SubMesh>();
        public readonly List<ShapeVertex> ShapeVertices = new List<ShapeVertex>();
        public readonly List<Shape> Shapes = new List<Shape>();
        public readonly List<ShapeChannel> ShapeChannels = new List<ShapeChannel>();
        public float[] ShapeFullWeights;
        public readonly List<float[]> BindPoses = new List<float[]>(); // 16 each, column-major as Unity stores them
        public uint[] BoneNameHashes;
        public uint RootBoneNameHash;
        public byte Compression;
        public bool Readable;
        public byte[] IndexBuffer;                                     // 16-bit indices
        public readonly List<float[]> SkinWeights = new List<float[]>(); // per vertex: 4 weights
        public readonly List<int[]> SkinBones = new List<int[]>();       // per vertex: 4 bone indices
        public uint CurrentChannels, VertexCount;
        public readonly List<Channel> Channels = new List<Channel>();
        public byte[] VertexData;
        public long EndOfVertexData; // where the compressed mesh starts (not read yet)

        public static MeshAsset Read(BinaryReader r)
        {
            var m = new MeshAsset { Name = AssetFile.ReadString(r) };
            for (int n = r.ReadInt32(), i = 0; i < n; i++)
            {
                var s = new SubMesh { FirstByte = r.ReadUInt32(), IndexCount = r.ReadUInt32(), Topology = r.ReadInt32() };
                s.FirstVertex = r.ReadUInt32();
                s.VertexCount = r.ReadUInt32();
                r.ReadBytes(24); // its bounds
                m.SubMeshes.Add(s);
            }
            // Blend shapes: every shape's changed vertices, the shapes, their named channels, full weights.
            for (int n = r.ReadInt32(), i = 0; i < n; i++)
                m.ShapeVertices.Add(new ShapeVertex { Vertex = Floats(r, 3), Normal = Floats(r, 3), Tangent = Floats(r, 3), Index = r.ReadUInt32() });
            for (int n = r.ReadInt32(), i = 0; i < n; i++)
            {
                var s = new Shape { FirstVertex = r.ReadUInt32(), VertexCount = r.ReadUInt32(), HasNormals = r.ReadBoolean(), HasTangents = r.ReadBoolean() };
                AssetFile.Align(r);
                m.Shapes.Add(s);
            }
            for (int n = r.ReadInt32(), i = 0; i < n; i++)
                m.ShapeChannels.Add(new ShapeChannel { Name = AssetFile.ReadString(r), NameHash = r.ReadUInt32(), FrameIndex = r.ReadInt32(), FrameCount = r.ReadInt32() });
            m.ShapeFullWeights = Floats(r, r.ReadInt32());
            for (int n = r.ReadInt32(), i = 0; i < n; i++) m.BindPoses.Add(Floats(r, 16));
            int hashes = r.ReadInt32();
            m.BoneNameHashes = new uint[hashes];
            for (int i = 0; i < hashes; i++) m.BoneNameHashes[i] = r.ReadUInt32();
            m.RootBoneNameHash = r.ReadUInt32();
            m.Compression = r.ReadByte();
            m.Readable = r.ReadBoolean();
            r.ReadBoolean(); // keep vertices
            r.ReadBoolean(); // keep indices
            AssetFile.Align(r);
            m.IndexBuffer = AssetFile.ReadBytes(r);
            for (int n = r.ReadInt32(), i = 0; i < n; i++)
            {
                m.SkinWeights.Add(Floats(r, 4));
                m.SkinBones.Add(new[] { r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32() });
            }
            m.CurrentChannels = r.ReadUInt32();
            m.VertexCount = r.ReadUInt32();
            for (int n = r.ReadInt32(), i = 0; i < n; i++)
                m.Channels.Add(new Channel { Stream = r.ReadByte(), Offset = r.ReadByte(), Format = r.ReadByte(), Dimension = r.ReadByte() });
            m.VertexData = AssetFile.ReadBytes(r);
            m.EndOfVertexData = r.BaseStream.Position;
            return m;
        }

        /// <summary>One vertex's value in a channel (0 position, 1 normal, 2 colour, 3-6 uv sets, 7 tangent), as
        /// floats, from 5.4's formats: 0 float, 1 half, 2 byte (0-1). Streams follow each other, 16-byte aligned.</summary>
        public float[] Get(int channel, int vertex)
        {
            if (streamStart == null)
            {
                streamStart = new int[4];
                streamStride = new int[4];
                foreach (var c in Channels) if (c.Dimension > 0) streamStride[c.Stream] += c.Dimension * FormatSize(c.Format);
                int offset = 0;
                for (int s = 0; s < 4; s++)
                {
                    streamStart[s] = offset;
                    offset = (offset + streamStride[s] * (int)VertexCount + 15) & ~15;
                }
            }
            var ch = Channels[channel];
            var values = new float[ch.Dimension];
            int at = streamStart[ch.Stream] + vertex * streamStride[ch.Stream] + ch.Offset;
            for (int i = 0; i < values.Length; i++)
                values[i] = ch.Format == 0 ? BitConverter.ToSingle(VertexData, at + i * 4)
                          : ch.Format == 1 ? Half(BitConverter.ToUInt16(VertexData, at + i * 2))
                          : VertexData[at + i] / 255f;
            return values;
        }

        public bool Has(int channel) => channel < Channels.Count && Channels[channel].Dimension > 0;

        private int[] streamStart, streamStride;

        private static int FormatSize(byte format) => format == 0 ? 4 : format == 1 ? 2 : 1;

        private static float Half(ushort h)
        {
            int sign = (h >> 15) & 1, exponent = (h >> 10) & 31, mantissa = h & 1023;
            float value = exponent == 0 ? mantissa / 1024f * (float)Math.Pow(2, -14)
                        : exponent == 31 ? float.PositiveInfinity
                        : (1 + mantissa / 1024f) * (float)Math.Pow(2, exponent - 15);
            return sign == 1 ? -value : value;
        }

        private static float[] Floats(BinaryReader r, int count)
        {
            var values = new float[count];
            for (int i = 0; i < count; i++) values[i] = r.ReadSingle();
            return values;
        }
    }
}
