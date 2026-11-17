using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Havoc;
using Havoc.IO.Tagfile.Binary;
using Havoc.Objects;
using Havoc.Reflection;
using static DSAnimStudio.HkxEdit.HkxAnimationFile;

namespace DSAnimStudio.HkxEdit
{
    /// <summary>
    /// Writes an <see cref="HkxAnimation"/> as an Elden Ring (Havok 2018.1) tagfile holding an uncompressed, interleaved animation:
    /// the input Havok Content Tools compresses into the game's spline format. The types come from the source file the animation
    /// was read from (its own, or its binder's compendium), so the result is laid out as the game's files are. The one type no game
    /// file carries, hkaInterleavedUncompressedAnimation, is declared here: hkaAnimation plus two arrays, transforms
    /// (hkQsTransform, one per track per frame) and floats, 96 bytes with the arrays at 64 and 80.
    /// </summary>
    public static class HkxInterleavedWriter
    {
        public static byte[] Write(HkxAnimationFile template, HkxAnimation a, IReadOnlyList<string> trackNames)
        {
            if (trackNames.Count != a.Tracks)
                throw new ArgumentException($"{trackNames.Count} track names for {a.Tracks} tracks");
            var container = template.Container;
            var animType = InterleavedType(template);

            // the reference frame: the source's type, or the default one it derives from (a directional frame's extra vector dropped)
            HkClass refFrame = null;
            if (a.Root != null)
            {
                var src = template.ReferenceFrame ?? throw new InvalidDataException("root motion without a reference frame in the source");
                var rfType = src.Type;
                while (rfType != null && rfType.Name != "hkaDefaultAnimatedReferenceFrame")
                    rfType = rfType.ParentType;
                rfType ??= src.Type;
                var fields = new Dictionary<HkField, IHkObject>();
                foreach (var f in rfType.AllFields)
                {
                    if (f.Name == "duration")
                        fields[f] = MakeFloat(f.Type, a.Duration);
                    else if (f.Name == "referenceFrameSamples")
                        fields[f] = new HkArray(f.Type, a.Root.Select(r => MakeVec4(f.Type.SubType, r)).ToList());
                    else if (Field(src, f.Name) is IHkObject v)
                        fields[f] = v;
                }
                refFrame = new HkClass(rfType, fields);
            }

            // the animation: hkaAnimation's fields, then the frames
            var animFields = new Dictionary<HkField, IHkObject>();
            foreach (var f in animType.AllFields)
            {
                switch (f.Name)
                {
                    case "type": animFields[f] = MakeInt(f.Type, 1); break;   // HK_INTERLEAVED_ANIMATION
                    case "duration": animFields[f] = MakeFloat(f.Type, a.Duration); break;
                    case "numberOfTransformTracks": animFields[f] = MakeInt(f.Type, a.Tracks); break;
                    case "numberOfFloatTracks": animFields[f] = MakeInt(f.Type, 0); break;
                    case "extractedMotion": animFields[f] = new HkPtr(f.Type, refFrame); break;
                    case "annotationTracks": animFields[f] = AnnotationTracks(f.Type, trackNames); break;
                    case "transforms": animFields[f] = Transforms(f.Type, a); break;
                    case "floats": animFields[f] = new HkArray(f.Type, new List<IHkObject>()); break;
                    default:
                        if (Field(template.Animation, f.Name) is IHkObject v && f.Name == "propertyBag")
                            animFields[f] = v;
                        break;
                }
            }
            var anim = new HkClass(animType, animFields);
            // one pointer object for both references: Havoc's writer gives each pointer object its own copy of what it points to,
            // and Havok Content Tools, finding the binding on a second animation, left that one uncompressed in the file
            var animPtrType = container.Type.AllFields.First(f => f.Name == "animations").Type.SubType;
            var animPtr = new HkPtr(animPtrType, anim);

            // the binding: the source's, on the new tracks
            var bindFields = new Dictionary<HkField, IHkObject>();
            foreach (var f in template.Binding.Type.AllFields)
            {
                switch (f.Name)
                {
                    case "animation": bindFields[f] = animPtr; break;
                    case "transformTrackToBoneIndices":
                        bindFields[f] = new HkArray(f.Type, a.Bones.Select(b => MakeInt(f.Type.SubType, b)).ToList());
                        break;
                    case "floatTrackToFloatSlotIndices":
                    case "partitionIndices":
                        bindFields[f] = new HkArray(f.Type, new List<IHkObject>());
                        break;
                    default:
                        if (Field(template.Binding, f.Name) is IHkObject v)
                            bindFields[f] = v;
                        break;
                }
            }
            var binding = new HkClass(template.Binding.Type, bindFields);

            // the container: this animation and its binding, nothing else (a raw export's skeletons and skins left out)
            var contFields = new Dictionary<HkField, IHkObject>();
            foreach (var f in container.Type.AllFields)
            {
                if (f.Name == "animations")
                    contFields[f] = new HkArray(f.Type, new List<IHkObject> { animPtr });
                else if (f.Name == "bindings")
                    contFields[f] = new HkArray(f.Type, new List<IHkObject> { new HkPtr(f.Type.SubType, binding) });
                else if (f.Type.Format == HkTypeFormat.Array && !f.Type.IsFixedSize)
                    contFields[f] = new HkArray(f.Type, new List<IHkObject>());
                else if (Field(container, f.Name) is IHkObject v)
                    contFields[f] = v;
            }
            var newContainer = new HkClass(container.Type, contFields);

            // the root: one named variant, the container (as the game's and Havok Content Tools' animation files have it)
            var root = Class(template.Root);
            var variantsField = root.Value.Keys.First(k => k.Name == "namedVariants");
            var srcVariant = Items(root.Value[variantsField]).Select(Class)
                .First(v => Class(Field(v, "variant")?.Value as IHkObject) == container);
            var varFields = new Dictionary<HkField, IHkObject>();
            foreach (var kv in srcVariant.Value)
                varFields[kv.Key] = kv.Key.Name == "variant" ? new HkPtr(kv.Key.Type, newContainer) : kv.Value;
            var rootFields = new Dictionary<HkField, IHkObject>(root.Value)
            {
                [variantsField] = new HkArray(variantsField.Type, new List<IHkObject> { new HkClass(srcVariant.Type, varFields) }),
            };
            var newRoot = new HkClass(root.Type, rootFields);

            using var ms = new MemoryStream();
            HkBinaryTagfileWriter.Write(ms, true, newRoot, HkSdkVersion.V20180100);
            return FixTypeCount(ms.ToArray());
        }

        // ---- Havoc's type count

        // Havok counts the empty type slot 0 in the type name list (TNA1): a file with N types says N + 1, and its type indices run
        // 1..N. Havoc writes N, so Havok's reader takes the last type's index as out of bounds ("Invalid field type ID (out of
        // bounds)") and Havok Content Tools refuses the file. The count is rewritten, the chunk sizes around it with it.
        static byte[] FixTypeCount(byte[] d)
        {
            var root = ReadChunk(d, 0);
            if (root.Tag != "TAG0")
                throw new InvalidDataException("Havoc wrote no TAG0 tagfile");
            var type = root.Children.FirstOrDefault(c => c.Tag == "TYPE") ?? throw new InvalidDataException("Havoc's tagfile has no TYPE section");
            var names = type.Children.FirstOrDefault(c => c.Tag == "TNA1" || c.Tag == "TNAM")
                        ?? throw new InvalidDataException("Havoc's tagfile has no type name list");
            int pos = 0;
            long count = ReadPacked(names.Payload, ref pos);
            var payload = new List<byte>(WritePacked(count + 1));
            payload.AddRange(names.Payload.Skip(pos));
            while (payload.Count % 4 != 0)
                payload.Add(0);
            names.Payload = payload.ToArray();
            using var ms = new MemoryStream();
            WriteChunk(ms, root);
            return ms.ToArray();
        }

        sealed class Chunk
        {
            public uint Flags;          // the size word's top two bits
            public string Tag;
            public byte[] Payload;      // a leaf's bytes
            public List<Chunk> Children;   // a section's chunks
        }

        static Chunk ReadChunk(byte[] d, int at)
        {
            uint word = (uint)(d[at] << 24 | d[at + 1] << 16 | d[at + 2] << 8 | d[at + 3]);
            int size = (int)(word & 0x3FFFFFFF);
            var c = new Chunk { Flags = word & 0xC0000000, Tag = System.Text.Encoding.ASCII.GetString(d, at + 4, 4) };
            if (c.Tag is "TAG0" or "TYPE" or "INDX")
            {
                c.Children = new List<Chunk>();
                for (int i = at + 8; i + 8 <= at + size;)
                {
                    var child = ReadChunk(d, i);
                    c.Children.Add(child);
                    int cs = (int)(((uint)(d[i] << 24 | d[i + 1] << 16 | d[i + 2] << 8 | d[i + 3])) & 0x3FFFFFFF);
                    if (cs < 8)
                        break;
                    i += cs;
                }
            }
            else
            {
                c.Payload = d.AsSpan(at + 8, size - 8).ToArray();
            }
            return c;
        }

        static void WriteChunk(Stream s, Chunk c)
        {
            long start = s.Position;
            s.Write(new byte[8]);
            if (c.Children != null)
                foreach (var child in c.Children)
                    WriteChunk(s, child);
            else
                s.Write(c.Payload);
            long end = s.Position;
            uint word = (uint)(end - start) | c.Flags;
            s.Position = start;
            s.Write(new[] { (byte)(word >> 24), (byte)(word >> 16), (byte)(word >> 8), (byte)word });
            s.Write(System.Text.Encoding.ASCII.GetBytes(c.Tag));
            s.Position = end;
        }

        // Havok's packed integers: 7 bits, then 14 bits behind 10, 21 behind 110, 27 behind 11100
        static long ReadPacked(byte[] d, ref int i)
        {
            byte b = d[i];
            if ((b & 0x80) == 0) { i += 1; return b; }
            if ((b & 0xC0) == 0x80) { i += 2; return ((b << 8) | d[i - 1]) & 0x3FFF; }
            if ((b & 0xE0) == 0xC0) { i += 3; return ((b << 16) | (d[i - 2] << 8) | d[i - 1]) & 0x1FFFFF; }
            if ((b & 0xF8) == 0xE0) { i += 4; return ((b << 24) | (d[i - 3] << 16) | (d[i - 2] << 8) | d[i - 1]) & 0x7FFFFFF; }
            throw new InvalidDataException($"a packed integer starting 0x{b:X2} is beyond what a type count needs");
        }

        static byte[] WritePacked(long v)
        {
            if (v < 0x80) return new[] { (byte)v };
            if (v < 0x4000) return new[] { (byte)(0x80 | (v >> 8)), (byte)v };
            if (v < 0x200000) return new[] { (byte)(0xC0 | (v >> 16)), (byte)(v >> 8), (byte)v };
            if (v < 0x8000000) return new[] { (byte)(0xE0 | (v >> 24)), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
            throw new InvalidDataException($"{v} types");
        }

        // ---- types

        // hkaInterleavedUncompressedAnimation: the source's own when it is one, else declared on hkaAnimation
        static HkType InterleavedType(HkxAnimationFile template)
        {
            var srcType = template.Animation.Type;
            if (srcType.Name == "hkaInterleavedUncompressedAnimation")
                return srcType;
            var baseType = srcType.ParentType;
            if (baseType?.Name != "hkaAnimation")
                throw new InvalidDataException($"{srcType.Name} doesn't derive from hkaAnimation");
            // hkArray<hkQsTransform> and hkArray<hkReal> as hkaSkeleton declares them (the container's skeletons field leads there)
            var skelField = template.Container.Type.AllFields.First(f => f.Name == "skeletons");
            var skeleton = skelField.Type.SubType;
            while (skeleton != null && skeleton.Format == HkTypeFormat.Ptr)
                skeleton = skeleton.SubType;
            var qsArray = skeleton?.AllFields.FirstOrDefault(f => f.Name == "referencePose")?.Type;
            var realArray = skeleton?.AllFields.FirstOrDefault(f => f.Name == "referenceFloats")?.Type;
            if (qsArray == null || realArray == null)
                throw new InvalidDataException("the source's types have no hkaSkeleton to take hkQsTransform and hkReal arrays from");
            // an array field's flags as Havok gives them in the spline animation (36 in Havok Content Tools' files)
            var flagsFrom = srcType.AllFields.FirstOrDefault(f => f.Type.Format == HkTypeFormat.Array && !f.Type.IsFixedSize);
            var flags = flagsFrom?.Flags ?? (HkFieldFlags)36;
            int arrayAt = Align(baseType.ByteSize, 8);
            if (arrayAt != 64)
                throw new InvalidDataException($"hkaAnimation is {baseType.ByteSize} bytes, not the 64 of Elden Ring's Havok 2018.1");
            var type = NewType("hkaInterleavedUncompressedAnimation", baseType,
                HkTypeFlags.HasFormatInfo | HkTypeFlags.HasVersion | HkTypeFlags.HasByteSize | HkTypeFlags.HasFields,
                formatInfo: baseType.FormatInfo, version: 1, byteSize: arrayAt + 32, alignment: Math.Max(baseType.Alignment, 8));
            AddField(type, NewField("transforms", flags, arrayAt, qsArray));
            AddField(type, NewField("floats", flags, arrayAt + 16, realArray));
            return type;
        }

        static int Align(int x, int a) => (x + a - 1) / a * a;

        // Havoc's types have internal constructors and setters (they are only ever read from files): set through reflection
        const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        static HkType NewType(string name, HkType parent, HkTypeFlags flags, int formatInfo, int version, int byteSize, int alignment)
        {
            var t = (HkType)Activator.CreateInstance(typeof(HkType), nonPublic: true);
            typeof(HkType).GetProperty("Name", Any)!.SetValue(t, name);
            typeof(HkType).GetProperty("ParentType", Any)!.SetValue(t, parent);
            typeof(HkType).GetProperty("Flags", Any)!.SetValue(t, flags);
            typeof(HkType).GetField("mFormatInfo", Any)!.SetValue(t, formatInfo);
            typeof(HkType).GetField("mVersion", Any)!.SetValue(t, version);
            typeof(HkType).GetField("mByteSize", Any)!.SetValue(t, byteSize);
            typeof(HkType).GetField("mAlignment", Any)!.SetValue(t, alignment);
            return t;
        }

        static HkField NewField(string name, HkFieldFlags flags, int offset, HkType type)
        {
            var f = (HkField)Activator.CreateInstance(typeof(HkField), nonPublic: true);
            typeof(HkField).GetProperty("Name", Any)!.SetValue(f, name);
            typeof(HkField).GetProperty("Flags", Any)!.SetValue(f, flags);
            typeof(HkField).GetProperty("ByteOffset", Any)!.SetValue(f, offset);
            typeof(HkField).GetProperty("Type", Any)!.SetValue(f, type);
            return f;
        }

        static void AddField(HkType t, HkField f) =>
            ((List<HkField>)typeof(HkType).GetField("mFields", Any)!.GetValue(t)!).Add(f);

        // ---- values

        static IHkObject MakeInt(HkType t, long v)
        {
            while (t.Format != HkTypeFormat.Int && t.SubType != null && t.SubType != t)
                t = t.SubType;   // an enum or a typedef: its storage type
            return (t.BitCount, t.IsSigned) switch
            {
                (8, true) => new HkSByte(t, (sbyte)v),
                (8, false) => new HkByte(t, (byte)v),
                (16, true) => new HkInt16(t, (short)v),
                (16, false) => new HkUInt16(t, (ushort)v),
                (32, true) => new HkInt32(t, (int)v),
                (32, false) => new HkUInt32(t, (uint)v),
                (64, true) => new HkInt64(t, v),
                (64, false) => new HkUInt64(t, (ulong)v),
                _ => throw new InvalidDataException($"{t.Name}: an integer of {t.BitCount} bits"),
            };
        }

        static IHkObject MakeFloat(HkType t, float v)
        {
            if (t.IsSingle) return new HkSingle(t, v);
            if (t.IsDouble) return new HkDouble(t, v);
            if (t.IsHalf) return new HkHalf(t, (Half)v);
            throw new InvalidDataException($"{t.Name} is not a float type");
        }

        // a hkVector4 / hkQuaternion: a fixed array of four floats, or a class around one
        static IHkObject MakeVec4(HkType t, Vector4 v)
        {
            if (t.Format == HkTypeFormat.Array && t.IsFixedSize)
            {
                var e = t.SubType;
                return new HkArray(t, new List<IHkObject> { MakeFloat(e, v.X), MakeFloat(e, v.Y), MakeFloat(e, v.Z), MakeFloat(e, v.W) });
            }
            if (t.Format == HkTypeFormat.Class)
            {
                var fields = t.AllFields.ToList();
                if (fields.Count == 1)
                    return new HkClass(t, new Dictionary<HkField, IHkObject> { [fields[0]] = MakeVec4(fields[0].Type, v) });
            }
            throw new InvalidDataException($"{t.Name} is not a four-float vector");
        }

        static IHkObject Transforms(HkType arrayType, HkxAnimation a)
        {
            var qs = arrayType.SubType;
            var fT = qs.AllFields.First(f => f.Name == "translation");
            var fR = qs.AllFields.First(f => f.Name == "rotation");
            var fS = qs.AllFields.First(f => f.Name == "scale");
            var list = new List<IHkObject>(a.Frames * a.Tracks);
            for (int f = 0; f < a.Frames; f++)
            {
                for (int k = 0; k < a.Tracks; k++)
                {
                    var t = a.T[f, k];
                    var q = a.Q[f, k];
                    var s = a.S[f, k];
                    list.Add(new HkClass(qs, new Dictionary<HkField, IHkObject>
                    {
                        [fT] = MakeVec4(fT.Type, new Vector4(t, 1)),
                        [fR] = MakeVec4(fR.Type, new Vector4(q.X, q.Y, q.Z, q.W)),
                        [fS] = MakeVec4(fS.Type, new Vector4(s, 1)),
                    }));
                }
            }
            return new HkArray(arrayType, list);
        }

        static IHkObject AnnotationTracks(HkType arrayType, IReadOnlyList<string> names)
        {
            var track = arrayType.SubType;
            var fName = track.AllFields.First(f => f.Name == "trackName");
            var fAnn = track.AllFields.First(f => f.Name == "annotations");
            return new HkArray(arrayType, names.Select(n => (IHkObject)new HkClass(track, new Dictionary<HkField, IHkObject>
            {
                [fName] = new HkString(fName.Type, n),
                [fAnn] = new HkArray(fAnn.Type, new List<IHkObject>()),
            })).ToList());
        }
    }
}
