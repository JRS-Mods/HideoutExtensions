using System;
using System.Collections.Generic;
using System.Linq;
using EFT;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace HideoutExtensions
{
    /// <summary>
    /// Real doorways through the hideout's own walls. The game keeps its wall meshes on the graphics card only, so each
    /// mesh a doorway goes through (wall, its shadow copy, its colliders, paint over it) is read back from there once,
    /// the doorway's box is cut out of a copy (triangles clipped to what's outside the box, every vertex attribute carried
    /// over), and the copy takes the original's place in its renderer or collider. The originals go back before every
    /// new layout and when the hideout unloads. If anything can't be cut, or a doorway is still blocked after, nothing
    /// is changed and the caller keeps its teleporting doorways.
    /// </summary>
    internal static class WallCut
    {
        /// <summary>A doorway's box through a wall: from the floor to the doorway's top, a little past both wall faces.</summary>
        internal sealed class Hole
        {
            public string Name;
            public Vector3 Centre, Half;
            public Quaternion Rotation;
            public Matrix4x4 ToBox;
            public Bounds World;
        }

        private const float Margin = .06f, FloorClear = .01f;
        private static readonly Dictionary<Mesh, Source> sources = new Dictionary<Mesh, Source>();
        private static readonly List<Swap> swaps = new List<Swap>();
        private static string applied;
        private static bool appliedOk;

        private sealed class Swap { public MeshFilter Filter; public MeshCollider Collider; public Mesh Original, Cut; }

        /// <summary>The box through the wall between doorway sides <paramref name="a"/> (facing out of its wall) and <paramref name="b"/>.</summary>
        public static Hole Box(string name, Vector3 a, float facing, Vector3 b, float width, float height)
        {
            Quaternion turn = Quaternion.Euler(0, facing, 0);
            float depth = Mathf.Abs(Vector3.Dot(b - a, turn * Vector3.forward));
            Vector3 centre = (a + b) / 2;
            centre.y = a.y + (FloorClear + height) / 2;
            var hole = new Hole { Name = name, Centre = centre, Rotation = turn, Half = new Vector3(width / 2, (height - FloorClear) / 2, depth / 2 + Margin) };
            hole.ToBox = Matrix4x4.TRS(centre, turn, Vector3.one).inverse;
            var bounds = new Bounds(centre, Vector3.zero);
            for (int i = 0; i < 8; i++)
                bounds.Encapsulate(centre + turn * Vector3.Scale(hole.Half, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1)));
            hole.World = bounds;
            return hole;
        }

        /// <summary>
        /// Cuts every hole into whatever of <paramref name="scene"/> it goes through (leaving out <paramref name="skip"/>
        /// and players); true if they're all open, false (and the walls as the game has them) if not.
        /// </summary>
        public static bool Apply(Scene scene, Transform skip, IList<Hole> holes)
        {
            string key = string.Join("|", holes.Select(h => $"{h.Name}@{h.Centre}"));
            if (key == applied && swaps.All(s => (s.Filter != null || s.Collider != null) && s.Cut != null)) return appliedOk;
            Restore();
            applied = key; appliedOk = true;
            if (holes.Count == 0) return true;
            try
            {
                bool Skip(Component c) => c == null || c.gameObject.scene != scene || c.transform.IsChildOf(skip) || c.GetComponentInParent<Player>() != null;
                // Switched-off parts count too if they're an area's level (the game switches built levels on after the
                // hideout loads, and on upgrades); other switched-off parts of the scene are old pieces it never shows.
                bool Shown(Component c)
                {
                    if (c.gameObject.activeInHierarchy) return true;
                    for (Transform t = c.transform; t != null; t = t.parent) if (t.name.StartsWith("level", StringComparison.Ordinal)) return true;
                    return false;
                }
                var targets = new Dictionary<Component, List<Hole>>();
                var looked = new List<string>();
                foreach (GameObject top in scene.GetRootGameObjects())
                    foreach (Component part in top.GetComponentsInChildren<MeshFilter>(true).Where(f => f.sharedMesh != null && f.TryGetComponent(out MeshRenderer _)).Cast<Component>()
                                 .Concat(top.GetComponentsInChildren<MeshCollider>(true).Where(c => !c.convex && c.sharedMesh != null)))
                    {
                        if (Skip(part) || !Shown(part)) continue;
                        Mesh mesh = part is MeshFilter f ? f.sharedMesh : ((MeshCollider)part).sharedMesh;
                        Bounds bounds = WorldBounds(mesh.bounds, part.transform.localToWorldMatrix);
                        var list = holes.Where(h => bounds.Intersects(h.World)).ToList();
                        if (list.Count == 0) continue;
                        targets[part] = list;
                        looked.Add(Path(part.transform));
                    }

                var opened = new Dictionary<Hole, (bool seen, bool solid)>();
                foreach (var target in targets)
                {
                    var filter = target.Key as MeshFilter;
                    var collider = target.Key as MeshCollider;
                    Mesh original = filter != null ? filter.sharedMesh : collider.sharedMesh;
                    if (!sources.TryGetValue(original, out Source source)) sources[original] = source = Source.Read(original);
                    var through = new HashSet<Hole>();
                    Mesh cut = source.Cut(original, target.Key.transform.localToWorldMatrix, target.Value, through);
                    if (cut == null) continue;
                    foreach (Hole hole in through)
                    {
                        opened.TryGetValue(hole, out var o);
                        opened[hole] = (o.seen || filter != null, o.solid || collider != null);
                    }
                    swaps.Add(new Swap { Filter = filter, Collider = collider, Original = original, Cut = cut });
                    if (filter != null) filter.sharedMesh = cut;
                    else collider.sharedMesh = cut;
                }

                // A doorway nothing was cut for would be a frame on a solid wall: then not at all.
                foreach (Hole hole in holes)
                {
                    opened.TryGetValue(hole, out var o);
                    if (o.seen && o.solid) continue;
                    string missing = !o.seen && !o.solid ? "anything" : o.seen ? "a collider" : "a visible wall";
                    Plugin.Log.LogWarning($"'{hole.Name}': didn't find {missing} to cut where its doorway goes " +
                                          $"(looked at {(looked.Count == 0 ? "nothing" : string.Join(", ", looked))}); the doors teleport instead.");
                    Restore();
                    applied = key; appliedOk = false;
                    return false;
                }

                // Still blocked for you (something the cut can't open, like a box collider): then not at all.
                int player = LayerMask.NameToLayer("Player");
                int solid = 0;
                for (int layer = 0; layer < 32; layer++) if (player < 0 || !Physics.GetIgnoreLayerCollision(player, layer)) solid |= 1 << layer;
                foreach (Hole hole in holes)
                {
                    var inner = new Vector3(hole.Half.x - .12f, hole.Half.y - .15f, hole.Half.z - Margin);
                    var blocking = Physics.OverlapBox(hole.Centre, inner, hole.Rotation, solid, QueryTriggerInteraction.Ignore).Where(c => !Skip(c)).ToList();
                    if (blocking.Count == 0) continue;
                    Plugin.Log.LogWarning($"'{hole.Name}': its doorway is still blocked by {string.Join(", ", blocking.Select(c => $"{c.GetType().Name} {Path(c.transform)}"))}; the doors teleport instead.");
                    Restore();
                    applied = key; appliedOk = false;
                    return false;
                }
                Plugin.Log.LogInfo($"Doorways cut through the hideout's walls ({string.Join(", ", holes.Select(h => h.Name))}): " +
                                   string.Join(", ", swaps.Select(s => (s.Collider != null ? "collider " : "") + Path((s.Filter as Component ?? s.Collider).transform)
                                                                       + ((s.Filter as Component ?? s.Collider).gameObject.activeInHierarchy ? "" : " (not shown yet)"))) + ".");
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("The hideout's walls couldn't be cut for the doorways, so the doors teleport instead: " + e);
                Restore();
                applied = key; appliedOk = false;
                return false;
            }
        }

        /// <summary>Puts the game's own meshes back.</summary>
        public static void Restore()
        {
            foreach (Swap swap in swaps)
            {
                if (swap.Filter != null && swap.Filter.sharedMesh == swap.Cut) swap.Filter.sharedMesh = swap.Original;
                if (swap.Collider != null && swap.Collider.sharedMesh == swap.Cut) swap.Collider.sharedMesh = swap.Original;
                if (swap.Cut != null) UnityEngine.Object.Destroy(swap.Cut);
            }
            swaps.Clear();
            applied = null;
        }

        /// <summary>The hideout is gone: drop the copies and what was read back.</summary>
        public static void Forget()
        {
            Restore();
            sources.Clear();
        }

        private static Bounds WorldBounds(Bounds local, Matrix4x4 toWorld)
        {
            var bounds = new Bounds(toWorld.MultiplyPoint3x4(local.center), Vector3.zero);
            for (int i = 0; i < 8; i++)
                bounds.Encapsulate(toWorld.MultiplyPoint3x4(local.center + Vector3.Scale(local.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1))));
            return bounds;
        }

        private static string Path(Transform t)
        {
            var names = new List<string>();
            for (; t != null; t = t.parent) names.Add(t.name);
            names.Reverse();
            return string.Join("/", names);
        }

        /// <summary>A mesh as read back from the graphics card: its raw vertex streams, layout, indices and submeshes.</summary>
        private sealed class Source
        {
            private VertexAttributeDescriptor[] attributes;
            private int[] offsets, strides;
            private byte[][] streams;
            private int vertexCount;
            private IndexFormat indexFormat;
            private int[][] indices;
            private SubMeshDescriptor[] subMeshes;

            public static Source Read(Mesh mesh)
            {
                var source = new Source { attributes = mesh.GetVertexAttributes(), vertexCount = mesh.vertexCount, indexFormat = mesh.indexFormat };
                source.offsets = source.attributes.Select(a => mesh.GetVertexAttributeOffset(a.attribute)).ToArray();
                int streamCount = mesh.vertexBufferCount;
                source.streams = new byte[streamCount][];
                source.strides = new int[streamCount];
                for (int s = 0; s < streamCount; s++)
                    using (GraphicsBuffer buffer = mesh.GetVertexBuffer(s) ?? throw new InvalidOperationException($"{mesh.name} has no vertex buffer {s} on the graphics card"))
                    {
                        source.strides[s] = buffer.stride;
                        source.streams[s] = new byte[buffer.count * buffer.stride];
                        buffer.GetData(source.streams[s]);
                    }
                int[] all;
                using (GraphicsBuffer buffer = mesh.GetIndexBuffer() ?? throw new InvalidOperationException($"{mesh.name} has no index buffer on the graphics card"))
                {
                    var bytes = new byte[buffer.count * buffer.stride];
                    buffer.GetData(bytes);
                    all = new int[buffer.count];
                    for (int i = 0; i < all.Length; i++) all[i] = buffer.stride == 2 ? BitConverter.ToUInt16(bytes, i * 2) : (int)BitConverter.ToUInt32(bytes, i * 4);
                }
                source.subMeshes = new SubMeshDescriptor[mesh.subMeshCount];
                source.indices = new int[mesh.subMeshCount][];
                for (int i = 0; i < mesh.subMeshCount; i++)
                {
                    SubMeshDescriptor sub = mesh.GetSubMesh(i);
                    source.subMeshes[i] = sub;
                    source.indices[i] = new int[sub.indexCount];
                    for (int k = 0; k < sub.indexCount; k++) source.indices[i][k] = sub.baseVertex + all[sub.indexStart + k];
                }
                return source;
            }

            /// <summary>
            /// A copy with the holes cut out (holes and mesh placed by <paramref name="toWorld"/>), or null if none touch
            /// it; <paramref name="through"/> gets the holes that took something out.
            /// </summary>
            public Mesh Cut(Mesh original, Matrix4x4 toWorld, List<Hole> holes, HashSet<Hole> through)
            {
                var copy = new Copy(this);
                foreach (Hole hole in holes) if (copy.Cut(hole.ToBox * toWorld, hole.Half)) through.Add(hole);
                return through.Count > 0 ? copy.Build(original.name + " (doorways)") : null;
            }

            /// <summary>The mesh being cut: the source's vertices plus new ones, and its triangles so far.</summary>
            private sealed class Copy
            {
                private readonly Source source;
                private readonly byte[][] data;
                private readonly List<Vector3> positions = new List<Vector3>();
                private readonly List<int>[] triangles;
                private int count;
                private readonly int position;

                public Copy(Source source)
                {
                    this.source = source;
                    data = source.streams.Select(s => (byte[])s.Clone()).ToArray();
                    count = source.vertexCount;
                    triangles = source.indices.Select(i => new List<int>(i)).ToArray();
                    position = Array.FindIndex(source.attributes, a => a.attribute == VertexAttribute.Position);
                    if (position < 0) throw new InvalidOperationException("a mesh without positions");
                    var value = new float[4];
                    for (int v = 0; v < count; v++) { Decode(position, v, value); positions.Add(new Vector3(value[0], value[1], value[2])); }
                }

                private struct Point { public Vector3 At, Weight; }

                /// <summary>Takes out what's inside the box (half size <paramref name="half"/> about the origin of <paramref name="toBox"/>).</summary>
                public bool Cut(Matrix4x4 toBox, Vector3 half)
                {
                    bool changed = false;
                    for (int s = 0; s < triangles.Length; s++)
                    {
                        if (source.subMeshes[s].topology != MeshTopology.Triangles) continue;
                        List<int> list = triangles[s];
                        var kept = new List<int>(list.Count);
                        for (int t = 0; t + 2 < list.Count; t += 3)
                        {
                            int i0 = list[t], i1 = list[t + 1], i2 = list[t + 2];
                            Vector3 a = toBox.MultiplyPoint3x4(positions[i0]), b = toBox.MultiplyPoint3x4(positions[i1]), c = toBox.MultiplyPoint3x4(positions[i2]);
                            Vector3 min = Vector3.Min(a, Vector3.Min(b, c)), max = Vector3.Max(a, Vector3.Max(b, c));
                            if (max.x <= -half.x || min.x >= half.x || max.y <= -half.y || min.y >= half.y || max.z <= -half.z || min.z >= half.z)
                            {
                                kept.Add(i0); kept.Add(i1); kept.Add(i2);
                                continue;
                            }
                            changed = true;
                            var corners = new List<Point>
                            {
                                new Point { At = a, Weight = new Vector3(1, 0, 0) }, new Point { At = b, Weight = new Vector3(0, 1, 0) }, new Point { At = c, Weight = new Vector3(0, 0, 1) },
                            };
                            var made = new List<(Vector3 weight, int index)>();
                            // What's outside the box, in convex pieces: either side of it, then above and below, then in front and behind.
                            Emit(Clip(corners, 0, -1, half.x), i0, i1, i2, made, kept);
                            Emit(Clip(corners, 0, 1, half.x), i0, i1, i2, made, kept);
                            List<Point> across = Clip(Clip(corners, 0, 1, -half.x), 0, -1, -half.x);
                            Emit(Clip(across, 1, -1, half.y), i0, i1, i2, made, kept);
                            Emit(Clip(across, 1, 1, half.y), i0, i1, i2, made, kept);
                            List<Point> core = Clip(Clip(across, 1, 1, -half.y), 1, -1, -half.y);
                            Emit(Clip(core, 2, -1, half.z), i0, i1, i2, made, kept);
                            Emit(Clip(core, 2, 1, half.z), i0, i1, i2, made, kept);
                        }
                        triangles[s] = kept;
                    }
                    return changed;
                }

                // The part of the polygon where sign * coordinate >= limit.
                private static List<Point> Clip(List<Point> polygon, int axis, float sign, float limit)
                {
                    var result = new List<Point>(polygon.Count + 2);
                    for (int i = 0; i < polygon.Count; i++)
                    {
                        Point current = polygon[i], previous = polygon[(i + polygon.Count - 1) % polygon.Count];
                        float dc = sign * current.At[axis] - limit, dp = sign * previous.At[axis] - limit;
                        if ((dc >= 0) != (dp >= 0))
                        {
                            float t = dp / (dp - dc);
                            result.Add(new Point { At = Vector3.Lerp(previous.At, current.At, t), Weight = Vector3.Lerp(previous.Weight, current.Weight, t) });
                        }
                        if (dc >= 0) result.Add(current);
                    }
                    return result;
                }

                private void Emit(List<Point> polygon, int i0, int i1, int i2, List<(Vector3 weight, int index)> made, List<int> kept)
                {
                    if (polygon.Count < 3) return;
                    float area = 0;
                    for (int i = 1; i + 1 < polygon.Count; i++) area += Vector3.Cross(polygon[i].At - polygon[0].At, polygon[i + 1].At - polygon[0].At).magnitude;
                    if (area < 1e-6f) return;
                    var index = new int[polygon.Count];
                    for (int i = 0; i < polygon.Count; i++) index[i] = Vertex(polygon[i].Weight, i0, i1, i2, made);
                    for (int i = 1; i + 1 < polygon.Count; i++) { kept.Add(index[0]); kept.Add(index[i]); kept.Add(index[i + 1]); }
                }

                // A corner of the original triangle, or a new vertex blended from its three.
                private int Vertex(Vector3 w, int i0, int i1, int i2, List<(Vector3 weight, int index)> made)
                {
                    if (w.x > .99999f) return i0;
                    if (w.y > .99999f) return i1;
                    if (w.z > .99999f) return i2;
                    foreach (var m in made) if ((m.weight - w).sqrMagnitude < 1e-10f) return m.index;
                    int v = count++;
                    for (int s = 0; s < data.Length; s++)
                    {
                        int stride = source.strides[s];
                        if (data[s].Length < count * stride) Array.Resize(ref data[s], Math.Max(count * stride, data[s].Length * 2));
                        // Bytes no attribute covers (there shouldn't be any) come from the nearest corner.
                        int nearest = w.x >= w.y && w.x >= w.z ? i0 : w.y >= w.z ? i1 : i2;
                        Buffer.BlockCopy(data[s], nearest * stride, data[s], v * stride, stride);
                    }
                    var a = new float[4]; var b = new float[4]; var c = new float[4]; var o = new float[4];
                    for (int k = 0; k < source.attributes.Length; k++)
                    {
                        VertexAttributeFormat format = source.attributes[k].format;
                        if (format == VertexAttributeFormat.UInt8 || format == VertexAttributeFormat.SInt8 || format == VertexAttributeFormat.UInt16
                            || format == VertexAttributeFormat.SInt16 || format == VertexAttributeFormat.UInt32 || format == VertexAttributeFormat.SInt32) continue;
                        Decode(k, i0, a); Decode(k, i1, b); Decode(k, i2, c);
                        for (int d = 0; d < 4; d++) o[d] = a[d] * w.x + b[d] * w.y + c[d] * w.z;
                        VertexAttribute attribute = source.attributes[k].attribute;
                        if (attribute == VertexAttribute.Normal) { var n = new Vector3(o[0], o[1], o[2]).normalized; o[0] = n.x; o[1] = n.y; o[2] = n.z; }
                        if (attribute == VertexAttribute.Tangent) { var n = new Vector3(o[0], o[1], o[2]).normalized; o[0] = n.x; o[1] = n.y; o[2] = n.z; o[3] = a[3]; }
                        Encode(k, v, o);
                    }
                    var p = new float[4];
                    Decode(position, v, p);
                    positions.Add(new Vector3(p[0], p[1], p[2]));
                    made.Add((w, v));
                    return v;
                }

                private void Decode(int k, int vertex, float[] into)
                {
                    VertexAttributeDescriptor a = source.attributes[k];
                    byte[] bytes = data[a.stream];
                    int at = vertex * source.strides[a.stream] + source.offsets[k];
                    for (int d = 0; d < 4; d++) into[d] = 0;
                    for (int d = 0; d < a.dimension; d++)
                    {
                        switch (a.format)
                        {
                            case VertexAttributeFormat.Float32: into[d] = BitConverter.ToSingle(bytes, at + d * 4); break;
                            case VertexAttributeFormat.Float16: into[d] = Mathf.HalfToFloat(BitConverter.ToUInt16(bytes, at + d * 2)); break;
                            case VertexAttributeFormat.UNorm8: into[d] = bytes[at + d] / 255f; break;
                            case VertexAttributeFormat.SNorm8: into[d] = Mathf.Max((sbyte)bytes[at + d] / 127f, -1); break;
                            case VertexAttributeFormat.UNorm16: into[d] = BitConverter.ToUInt16(bytes, at + d * 2) / 65535f; break;
                            case VertexAttributeFormat.SNorm16: into[d] = Mathf.Max(BitConverter.ToInt16(bytes, at + d * 2) / 32767f, -1); break;
                            default: return;
                        }
                    }
                }

                private void Encode(int k, int vertex, float[] value)
                {
                    VertexAttributeDescriptor a = source.attributes[k];
                    byte[] bytes = data[a.stream];
                    int at = vertex * source.strides[a.stream] + source.offsets[k];
                    for (int d = 0; d < a.dimension; d++)
                    {
                        float x = value[d];
                        switch (a.format)
                        {
                            case VertexAttributeFormat.Float32: Write(bytes, at + d * 4, BitConverter.GetBytes(x)); break;
                            case VertexAttributeFormat.Float16: Write(bytes, at + d * 2, BitConverter.GetBytes(Mathf.FloatToHalf(x))); break;
                            case VertexAttributeFormat.UNorm8: bytes[at + d] = (byte)Mathf.RoundToInt(Mathf.Clamp01(x) * 255); break;
                            case VertexAttributeFormat.SNorm8: bytes[at + d] = (byte)(sbyte)Mathf.RoundToInt(Mathf.Clamp(x, -1, 1) * 127); break;
                            case VertexAttributeFormat.UNorm16: Write(bytes, at + d * 2, BitConverter.GetBytes((ushort)Mathf.RoundToInt(Mathf.Clamp01(x) * 65535))); break;
                            case VertexAttributeFormat.SNorm16: Write(bytes, at + d * 2, BitConverter.GetBytes((short)Mathf.RoundToInt(Mathf.Clamp(x, -1, 1) * 32767))); break;
                        }
                    }
                }

                private static void Write(byte[] into, int at, byte[] value) => Buffer.BlockCopy(value, 0, into, at, value.Length);

                public Mesh Build(string name)
                {
                    IndexFormat format = count > 65535 ? IndexFormat.UInt32 : source.indexFormat;
                    var mesh = new Mesh { name = name, indexFormat = format };
                    mesh.SetVertexBufferParams(count, source.attributes);
                    for (int s = 0; s < data.Length; s++)
                    {
                        if (mesh.GetVertexBufferStride(s) != source.strides[s]) { UnityEngine.Object.Destroy(mesh); throw new InvalidOperationException($"{name}: vertex layout came back different"); }
                        mesh.SetVertexBufferData(data[s], 0, 0, count * source.strides[s], s);
                    }
                    int total = triangles.Sum(t => t.Count);
                    mesh.SetIndexBufferParams(total, format);
                    int start = 0;
                    var starts = new int[triangles.Length];
                    if (format == IndexFormat.UInt16)
                    {
                        var all = new ushort[total];
                        for (int s = 0; s < triangles.Length; s++) { starts[s] = start; foreach (int i in triangles[s]) all[start++] = (ushort)i; }
                        mesh.SetIndexBufferData(all, 0, 0, total);
                    }
                    else
                    {
                        var all = new int[total];
                        for (int s = 0; s < triangles.Length; s++) { starts[s] = start; foreach (int i in triangles[s]) all[start++] = i; }
                        mesh.SetIndexBufferData(all, 0, 0, total);
                    }
                    mesh.subMeshCount = triangles.Length;
                    for (int s = 0; s < triangles.Length; s++) mesh.SetSubMesh(s, new SubMeshDescriptor(starts[s], triangles[s].Count, source.subMeshes[s].topology));
                    mesh.RecalculateBounds();
                    mesh.UploadMeshData(false);
                    return mesh;
                }
            }
        }
    }
}
