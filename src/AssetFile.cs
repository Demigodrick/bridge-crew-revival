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
            }
        }

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

        private static float[] Floats(BinaryReader r, int count)
        {
            var values = new float[count];
            for (int i = 0; i < count; i++) values[i] = r.ReadSingle();
            return values;
        }
    }
}
