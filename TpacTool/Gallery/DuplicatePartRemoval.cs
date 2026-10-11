using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using TpacTool.IO;
using TpacTool.Lib;

namespace TpacTool
{
	/// <summary>
	/// Drops repeated geometry from the meshes written into one file. Every connected part of a mesh (what
	/// Blender's "select linked" picks: faces joined through shared vertices) that is byte identical to a part
	/// already kept, with the same material, is removed; a mesh that only repeats other parts is left out
	/// entirely (<see cref="RemovedMeshes"/>). Faces, vertices and positions of the changed meshes are compacted
	/// in place; dispose to restore them. Hold <see cref="ModelExporter.SyncRoot"/> while it is applied.
	/// Meshes with morph targets are left alone.
	/// </summary>
	internal sealed class DuplicatePartRemoval : IDisposable
	{
		private readonly List<Action> _restores = new List<Action>();
		private bool _disposed;

		/// <summary>Meshes whose every part repeats one kept earlier: leave them out of the export.</summary>
		public HashSet<Mesh> RemovedMeshes { get; } = new HashSet<Mesh>();

		/// <summary>How many connected parts were dropped (the parts of <see cref="RemovedMeshes"/> included).</summary>
		public int RemovedParts { get; private set; }

		public static DuplicatePartRemoval Apply(IEnumerable<Mesh> meshes)
		{
			var result = new DuplicatePartRemoval();
			try
			{
				var seen = new HashSet<string>();
				var visited = new HashSet<MeshEditData>();
				using (var sha = SHA256.Create())
				{
					foreach (var mesh in meshes)
					{
						var data = mesh.EditData?.Data;
						if (data == null || data.Faces.Length == 0 || data.MorphFrames.Count > 0 || !visited.Add(data))
							continue;
						result.Process(mesh, data, seen, sha);
					}
				}
			}
			catch
			{
				result.Dispose();
				throw;
			}
			return result;
		}

		private void Process(Mesh mesh, MeshEditData data, HashSet<string> seen, SHA256 sha)
		{
			var faces = data.Faces;
			var vertices = data.Vertices;
			var positions = data.Positions;
			foreach (var face in faces)
			{
				for (int c = 0; c < 3; c++)
				{
					var v = face[c];
					if (v < 0 || v >= vertices.Length || vertices[v].PositionIndex >= positions.Length)
						return; // malformed: leave it as it is
				}
			}
			var bones = data.Bones;
			var hasBones = bones != null && positions.Length > 0 && bones.Length >= positions.Length;

			// connected parts: faces sharing a position are linked, like blender's "select linked"
			var parent = new int[positions.Length];
			for (int i = 0; i < parent.Length; i++)
				parent[i] = i;
			int Find(int i)
			{
				while (parent[i] != i)
					i = parent[i] = parent[parent[i]];
				return i;
			}
			int Pos(int vertex) => (int) vertices[vertex].PositionIndex;
			foreach (var face in faces)
			{
				parent[Find(Pos(face.V1))] = Find(Pos(face.V0));
				parent[Find(Pos(face.V2))] = Find(Pos(face.V0));
			}
			var parts = new List<List<int>>();
			var partOfRoot = new Dictionary<int, List<int>>();
			for (int f = 0; f < faces.Length; f++)
			{
				var root = Find(Pos(faces[f].V0));
				if (!partOfRoot.TryGetValue(root, out var part))
				{
					partOfRoot[root] = part = new List<int>();
					parts.Add(part);
				}
				part.Add(f);
			}

			var material = MaterialKey(mesh);
			var corners = new byte[vertices.Length][];
			byte[] Corner(int v) => corners[v] ?? (corners[v] = CornerKey(vertices[v], positions, bones, hasBones));
			var keep = new bool[faces.Length];
			int removedFaces = 0, removedParts = 0;
			foreach (var part in parts)
			{
				// the part's triangles in a fixed order, so the same part matches however its faces are numbered
				var triangles = new List<byte[]>(part.Count);
				foreach (var f in part)
					triangles.Add(TriangleKey(Corner(faces[f].V0), Corner(faces[f].V1), Corner(faces[f].V2)));
				triangles.Sort(Compare);
				using (var stream = new MemoryStream())
				{
					var name = System.Text.Encoding.UTF8.GetBytes(material);
					stream.Write(name, 0, name.Length);
					stream.WriteByte(0);
					foreach (var triangle in triangles)
						stream.Write(triangle, 0, triangle.Length);
					var key = Convert.ToBase64String(sha.ComputeHash(stream.GetBuffer(), 0, (int) stream.Length));
					if (seen.Add(key))
					{
						foreach (var f in part)
							keep[f] = true;
					}
					else
					{
						removedParts++;
						removedFaces += part.Count;
					}
				}
			}
			if (removedFaces == 0)
				return;
			RemovedParts += removedParts;
			if (removedFaces == faces.Length)
			{
				RemovedMeshes.Add(mesh);
				return;
			}
			Compact(data, keep, hasBones);
		}

		/// <summary>Keeps the faces marked in <paramref name="keep"/> and only the vertices / positions they use.</summary>
		private void Compact(MeshEditData data, bool[] keep, bool hasBones)
		{
			var faces = data.Faces;
			var vertices = data.Vertices;
			var positions = data.Positions;
			var bones = data.Bones;
			var masks = data.FaceSmoothingGroupMasks;
			_restores.Add(() =>
			{
				data.Faces = faces;
				data.Vertices = vertices;
				data.Positions = positions;
				data.Bones = bones;
				data.FaceSmoothingGroupMasks = masks;
			});

			var vertexMap = new int[vertices.Length];
			var positionMap = new int[positions.Length];
			for (int i = 0; i < vertexMap.Length; i++)
				vertexMap[i] = -1;
			for (int i = 0; i < positionMap.Length; i++)
				positionMap[i] = -1;
			var newFaces = new List<MeshEditData.Face>();
			var newMasks = new List<int>();
			var newVertices = new List<MeshEditData.Vertex>();
			var usedPositions = new List<int>();
			for (int f = 0; f < faces.Length; f++)
			{
				if (!keep[f])
					continue;
				var face = faces[f];
				for (int c = 0; c < 3; c++)
				{
					var v = face[c];
					if (vertexMap[v] < 0)
					{
						var vertex = vertices[v];
						var p = (int) vertex.PositionIndex;
						if (positionMap[p] < 0)
						{
							positionMap[p] = usedPositions.Count;
							usedPositions.Add(p);
						}
						vertex.PositionIndex = (uint) positionMap[p];
						vertexMap[v] = newVertices.Count;
						newVertices.Add(vertex);
					}
					face[c] = vertexMap[v];
				}
				newFaces.Add(face);
				if (masks != null && masks.Length == faces.Length)
					newMasks.Add(masks[f]);
			}

			data.Faces = newFaces.ToArray();
			data.Vertices = newVertices.ToArray();
			data.Positions = usedPositions.ConvertAll(p => positions[p]).ToArray();
			if (hasBones)
				data.Bones = usedPositions.ConvertAll(p => bones[p]).ToArray();
			if (masks != null && masks.Length == faces.Length)
				data.FaceSmoothingGroupMasks = newMasks.ToArray();
		}

		private static string MaterialKey(Mesh mesh)
		{
			var first = mesh.Material != null && mesh.Material.TryGetItem(out var material) ? material.Name : string.Empty;
			var second = mesh.SecondMaterial != null && mesh.SecondMaterial.TryGetItem(out var material2) ? material2.Name : string.Empty;
			return first + "|" + second;
		}

		/// <summary>
		/// A corner's bytes: the vertex (normal, tangent frame, uvs, colors) without its position index, the
		/// position and, for skinned meshes, the bone weights.
		/// </summary>
		private static unsafe byte[] CornerKey(MeshEditData.Vertex vertex, Vector4[] positions,
			MeshEditData.Bone[] bones, bool hasBones)
		{
			var vertexSize = sizeof(MeshEditData.Vertex) - sizeof(uint);
			const int positionSize = 3 * sizeof(float); // the 4th component is padding
			var boneSize = hasBones ? sizeof(MeshEditData.Bone) : 0;
			var key = new byte[vertexSize + positionSize + boneSize];
			var position = positions[vertex.PositionIndex];
			fixed (byte* dst = key)
			{
				var v = (byte*) &vertex + sizeof(uint); // PositionIndex is the first field
				for (int i = 0; i < vertexSize; i++)
					dst[i] = v[i];
				var p = (byte*) &position;
				for (int i = 0; i < positionSize; i++)
					dst[vertexSize + i] = p[i];
				if (hasBones)
				{
					var bone = bones[vertex.PositionIndex];
					var b = (byte*) &bone;
					for (int i = 0; i < boneSize; i++)
						dst[vertexSize + positionSize + i] = b[i];
				}
			}
			return key;
		}

		/// <summary>The three corners, starting at the smallest one: the same triangle whichever corner comes first.</summary>
		private static byte[] TriangleKey(byte[] a, byte[] b, byte[] c)
		{
			var corners = new[] { a, b, c };
			int start = 0;
			for (int i = 1; i < 3; i++)
				if (Compare(corners[i], corners[start]) < 0)
					start = i;
			var key = new byte[a.Length * 3];
			for (int i = 0; i < 3; i++)
				Buffer.BlockCopy(corners[(start + i) % 3], 0, key, i * a.Length, a.Length);
			return key;
		}

		private static int Compare(byte[] x, byte[] y)
		{
			var length = Math.Min(x.Length, y.Length);
			for (int i = 0; i < length; i++)
			{
				if (x[i] != y[i])
					return x[i] < y[i] ? -1 : 1;
			}
			return x.Length.CompareTo(y.Length);
		}

		public void Dispose()
		{
			if (_disposed)
				return;
			_disposed = true;
			foreach (var restore in _restores)
				restore();
		}
	}
}
