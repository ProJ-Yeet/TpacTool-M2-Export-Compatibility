using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using OpenTK;
using OpenTK.Graphics.OpenGL4;
using TpacTool.Lib;
using static OpenTK.Graphics.OpenGL4.GL;

namespace TpacTool
{
	public static class MeshManager
	{
		private const int MAX_CACHE = 32;
		private static readonly LinkedList<(Mesh, OglMesh)> cache = new LinkedList<(Mesh, OglMesh)>();

		public static OglMesh Get(Mesh mesh)
		{
			LinkedListNode<(Mesh, OglMesh)> node;
			for (node = cache.First; node != null; node = node.Next)
			{
				if (node.Value.Item1 == mesh)
					break;
			}

			if (node != null)
			{
				if (node.Previous != null)
				{
					cache.Remove(node);
					cache.AddFirst(node);
				}

				return node.Value.Item2;
			}

			var m = new OglMesh(mesh);
			cache.AddFirst((mesh, m));

			while (cache.Count > MAX_CACHE)
			{
				cache.Last.Value.Item2.Release();
				cache.RemoveLast();
			}

			return m;
		}

		public static void Clear()
		{
			foreach (var (_, oglMesh) in cache)
			{
				oglMesh.Release();
			}
			cache.Clear();
		}

		public class OglMesh
		{
			private const int VBO_POSITION = 0;
			private const int VBO_NORMAL = 1;
			private const int VBO_UV = 2;
			private const int VBO_COLOR = 3;
			private const int VBO_COLOR2 = 4;
			private const int VBO_BONEID = 5;
			private const int VBO_BONEWEIGHT = 6;
			private const int VBO_INDEX = 7;
			private const int TOTAL_VBO = 8;

			private int _vaoId = -1;
			private readonly int _vertexCount;
			private int[] _vboIds;

			public OglMesh(Mesh mesh)
			{
				var vertexStream = mesh.VertexStream?.Data;
				var editData = mesh.EditData?.Data;
				if (vertexStream != null && vertexStream.Positions != null)
				{
					_vaoId = GenVertexArray();
					BindVertexArray(_vaoId);
					_vertexCount = vertexStream.Indices.Length;
					_vboIds = new int[TOTAL_VBO];
					GenBuffers(TOTAL_VBO, _vboIds);

					BindBuffer(BufferTarget.ElementArrayBuffer, _vboIds[VBO_INDEX]);
					BufferData(BufferTarget.ElementArrayBuffer,
						sizeof(uint) * vertexStream.Indices.Length,
						vertexStream.Indices,
						BufferUsageHint.StaticDraw);

					BindBuffer(BufferTarget.ArrayBuffer, _vboIds[VBO_POSITION]);
					BufferData(BufferTarget.ArrayBuffer, 
						3 * sizeof(float) * vertexStream.Positions.Length,
						vertexStream.Positions,
						BufferUsageHint.StaticDraw);
					VertexAttribPointer(VBO_POSITION, 3, VertexAttribPointerType.Float, false, 0, 0);
					EnableVertexAttribArray(VBO_POSITION);

					BindBuffer(BufferTarget.ArrayBuffer, _vboIds[VBO_NORMAL]);
					BufferData(BufferTarget.ArrayBuffer,
						3 * sizeof(float) * vertexStream.Normals.Length,
						vertexStream.Normals,
						BufferUsageHint.StaticDraw);
					VertexAttribPointer(VBO_NORMAL, 3, VertexAttribPointerType.Float, false, 0, 0);
					EnableVertexAttribArray(VBO_NORMAL);

					BindBuffer(BufferTarget.ArrayBuffer, _vboIds[VBO_UV]);
					BufferData(BufferTarget.ArrayBuffer,
						2 * sizeof(float) * vertexStream.Uv1.Length,
						vertexStream.Uv1,
						BufferUsageHint.StaticDraw);
					VertexAttribPointer(VBO_UV, 2, VertexAttribPointerType.Float, false, 0, 0);
					EnableVertexAttribArray(VBO_UV);

					if (vertexStream.Colors1?.Length > 0)
					{
						BindBuffer(BufferTarget.ArrayBuffer, _vboIds[VBO_COLOR]);
						BufferData(BufferTarget.ArrayBuffer,
							4 * sizeof(byte) * vertexStream.Colors1.Length,
							vertexStream.Colors1,
							BufferUsageHint.StaticDraw);
						VertexAttribPointer(VBO_COLOR, 4, VertexAttribPointerType.UnsignedByte, true, 0, 0);
						EnableVertexAttribArray(VBO_COLOR);
					}

					if (vertexStream.Colors2?.Length > 0)
					{
						BindBuffer(BufferTarget.ArrayBuffer, _vboIds[VBO_COLOR2]);
						BufferData(BufferTarget.ArrayBuffer,
							4 * sizeof(byte) * vertexStream.Colors2.Length,
							vertexStream.Colors2,
							BufferUsageHint.StaticDraw);
						VertexAttribPointer(VBO_COLOR2, 4, VertexAttribPointerType.UnsignedByte, true, 0, 0);
						EnableVertexAttribArray(VBO_COLOR2);
					}

					if (vertexStream.BoneIndices?.Length > 0)
					{
						BindBuffer(BufferTarget.ArrayBuffer, _vboIds[VBO_BONEID]);
						BufferData(BufferTarget.ArrayBuffer,
							4 * sizeof(byte) * vertexStream.BoneIndices.Length,
							vertexStream.BoneIndices,
							BufferUsageHint.StaticDraw);
						VertexAttribPointer(VBO_BONEID, 4, VertexAttribPointerType.UnsignedByte, false, 0, 0);
						EnableVertexAttribArray(VBO_BONEID);
					}

					if (vertexStream.BoneWeights?.Length > 0)
					{
						BindBuffer(BufferTarget.ArrayBuffer, _vboIds[VBO_BONEWEIGHT]);
						BufferData(BufferTarget.ArrayBuffer,
							4 * sizeof(byte) * vertexStream.BoneWeights.Length,
							vertexStream.BoneWeights,
							BufferUsageHint.StaticDraw);
						VertexAttribPointer(VBO_BONEWEIGHT, 4, VertexAttribPointerType.UnsignedByte, true, 0, 0);
						EnableVertexAttribArray(VBO_BONEWEIGHT);
					}

					BindVertexArray(0);
				}
				else if (editData != null)
				{
					_vaoId = GenVertexArray();
					BindVertexArray(_vaoId);
					_vertexCount = editData.Faces.Length * 3;
					_vboIds = new int[3];
					GenBuffers(3, _vboIds);

					BindBuffer(BufferTarget.ElementArrayBuffer, _vboIds[0]);
					BufferData(BufferTarget.ElementArrayBuffer,
						3 * sizeof(uint) * editData.Faces.Length,
						editData.Faces,
						BufferUsageHint.StaticDraw);

					var posArray = new System.Numerics.Vector4[editData.Vertices.Length];
					for (var i = 0; i < editData.Vertices.Length; i++)
					{
						posArray[i] = editData.Positions[editData.Vertices[i].PositionIndex];
					}

					BindBuffer(BufferTarget.ArrayBuffer, _vboIds[1]);
					BufferData(BufferTarget.ArrayBuffer,
						4 * sizeof(float) * posArray.Length,
						posArray,
						BufferUsageHint.StaticDraw);
					VertexAttribPointer(VBO_POSITION, 3, VertexAttribPointerType.Float, false, 4 * sizeof(float), 0);
					EnableVertexAttribArray(VBO_POSITION);

					var size = Marshal.SizeOf<MeshEditData.Vertex>();
					BindBuffer(BufferTarget.ArrayBuffer, _vboIds[2]);
					BufferData(BufferTarget.ArrayBuffer,
						size * editData.Vertices.Length,
						editData.Vertices,
						BufferUsageHint.StaticDraw);
					VertexAttribPointer(VBO_NORMAL, 3, VertexAttribPointerType.Float, false, size, 4);
					VertexAttribPointer(VBO_UV, 2, VertexAttribPointerType.Float, false, size, 68);
					VertexAttribPointer(VBO_COLOR, 4, VertexAttribPointerType.UnsignedByte, true, size, 84);
					VertexAttribPointer(VBO_COLOR2, 4, VertexAttribPointerType.UnsignedByte, true, size, 88);
					EnableVertexAttribArray(VBO_NORMAL);
					EnableVertexAttribArray(VBO_UV);
					EnableVertexAttribArray(VBO_COLOR);
					EnableVertexAttribArray(VBO_COLOR2);

					BindVertexArray(0);
				}
			}

			public OglMesh(int[] indices, float[] positions, 
				float[] normals = null, float[] uvs = null)
			{
				_vaoId = GenVertexArray();
				BindVertexArray(_vaoId);

				_vertexCount = indices.Length;
				_vboIds = new int[4];
				GenBuffers(4, _vboIds);

				BindBuffer(BufferTarget.ElementArrayBuffer, _vboIds[0]);
				BufferData(BufferTarget.ElementArrayBuffer,
					sizeof(uint) * indices.Length,
					indices,
					BufferUsageHint.StaticDraw);

				BindBuffer(BufferTarget.ArrayBuffer, _vboIds[1]);
				BufferData(BufferTarget.ArrayBuffer,
					sizeof(float) * positions.Length,
					positions,
					BufferUsageHint.StaticDraw);
				VertexAttribPointer(VBO_POSITION, 3, VertexAttribPointerType.Float, false, 0, 0);
				EnableVertexAttribArray(VBO_POSITION);

				if (normals != null)
				{
					BindBuffer(BufferTarget.ArrayBuffer, _vboIds[2]);
					BufferData(BufferTarget.ArrayBuffer,
						sizeof(float) * normals.Length,
						normals,
						BufferUsageHint.StaticDraw);
					VertexAttribPointer(VBO_NORMAL, 3, VertexAttribPointerType.Float, false, 0, 0);
					EnableVertexAttribArray(VBO_NORMAL);
				}

				if (uvs != null)
				{
					BindBuffer(BufferTarget.ArrayBuffer, _vboIds[3]);
					BufferData(BufferTarget.ArrayBuffer,
						sizeof(float) * uvs.Length,
						uvs,
						BufferUsageHint.StaticDraw);
					VertexAttribPointer(VBO_UV, 2, VertexAttribPointerType.Float, false, 0, 0);
					EnableVertexAttribArray(VBO_UV);
				}
				
				BindVertexArray(0);
			}

			public void Draw()
			{
				if (_vaoId >= 0)
				{
					BindVertexArray(_vaoId);

					DrawElements(BeginMode.Triangles, _vertexCount, DrawElementsType.UnsignedInt, 0);

					BindVertexArray(0);
				}
			}

			public void DrawInstanced(int instance)
			{
				if (_vaoId >= 0)
				{
					BindVertexArray(_vaoId);

					DrawElementsInstanced(PrimitiveType.Triangles, _vertexCount, DrawElementsType.UnsignedInt, IntPtr.Zero, instance);

					BindVertexArray(0);
				}
			}

			public void Release()
			{
				if (_vaoId >= 0)
				{
					DeleteVertexArray(_vaoId);
					_vaoId = 0;
				}

				if (_vboIds != null)
				{
					DeleteBuffers(_vboIds.Length, _vboIds);
					_vboIds = null;
				}
			}

			public void DrawLines()
			{
				if (_vaoId >= 0)
				{
					BindVertexArray(_vaoId);
					DrawElements(PrimitiveType.Lines, _vertexCount, DrawElementsType.UnsignedInt, 0);
					BindVertexArray(0);
				}
			}
		}

		public static OglMesh CreateSkeletonMesh(SkeletonDefinitionData skeletonDef)
		{
			if (skeletonDef == null || skeletonDef.Bones.Count == 0)
				return null;

			var matrices = skeletonDef.CreateBoneMatrices();
			var positions = new List<float>();
			var indices = new List<uint>();

			uint index = 0;
			for (int i = 0; i < matrices.Length; i++)
			{
				var bone = skeletonDef.Bones[i];
				var worldMat = matrices[i];
				var bonePos = System.Numerics.Vector3.Transform(System.Numerics.Vector3.Zero, worldMat);

				positions.Add(bonePos.X);
				positions.Add(bonePos.Y);
				positions.Add(bonePos.Z);

				if (bone.Parent != null)
				{
					var parentIndex = skeletonDef.GetBoneId(bone.Parent);
					if (parentIndex >= 0)
					{
						var parentMat = matrices[parentIndex];
						var parentPos = System.Numerics.Vector3.Transform(System.Numerics.Vector3.Zero, parentMat);
						positions.Add(parentPos.X);
						positions.Add(parentPos.Y);
						positions.Add(parentPos.Z);

						indices.Add(index);
						indices.Add(index + 1);
						index += 2;
					}
					else
					{
						index++;
					}
				}
				else
				{
					index++;
				}
			}

			if (indices.Count == 0)
				return null;

			return new OglMesh(indices.Select(i => (int)i).ToArray(), positions.ToArray());
		}


		public static OglMesh CreateSkeletonMesh(SkeletonDefinitionData skeletonDef, System.Numerics.Matrix4x4[] matrices)
		{
			if (skeletonDef == null || matrices == null || matrices.Length < skeletonDef.Bones.Count) return CreateSkeletonMesh(skeletonDef);
			var positions = new List<float>(); var indices = new List<int>();
			for (var i=0;i<skeletonDef.Bones.Count;i++) { var parent=skeletonDef.GetBoneId(skeletonDef.Bones[i].Parent); if(parent<0 || parent>=matrices.Length) continue; var a=System.Numerics.Vector3.Transform(System.Numerics.Vector3.Zero, matrices[i]); var b=System.Numerics.Vector3.Transform(System.Numerics.Vector3.Zero, matrices[parent]); var n=positions.Count/3; positions.AddRange(new[]{a.X,a.Y,a.Z,b.X,b.Y,b.Z}); indices.Add(n); indices.Add(n+1); }
			return indices.Count == 0 ? null : new OglMesh(indices.ToArray(), positions.ToArray());
		}

		public static OglMesh CreateCapsuleMesh(float radius, float height, int segments = 16)
		{
			var positions = new List<float>();
			var normals = new List<float>();
			var uvs = new List<float>();
			var indices = new List<int>();

			int rings = segments;
			int sectors = segments * 2;

			float halfHeight = height / 2f;

			for (int i = 0; i <= rings; i++)
			{
				float phi = (float)Math.PI * i / rings;
				float sinPhi = (float)Math.Sin(phi);
				float cosPhi = (float)Math.Cos(phi);

				float y = cosPhi;
				float scale = sinPhi;

				for (int j = 0; j <= sectors; j++)
				{
					float theta = (float)(2 * Math.PI * j / sectors);
					float sinTheta = (float)Math.Sin(theta);
					float cosTheta = (float)Math.Cos(theta);

					float x = cosTheta * scale;
					float z = sinTheta * scale;

					positions.Add(x * radius);
					positions.Add(y * halfHeight + (y > 0 ? halfHeight : -halfHeight));
					positions.Add(z * radius);

					normals.Add(x);
					normals.Add(y);
					normals.Add(z);

					uvs.Add((float)j / sectors);
					uvs.Add((float)i / rings);
				}
			}

			for (int i = 0; i < rings; i++)
			{
				for (int j = 0; j < sectors; j++)
				{
					int current = i * (sectors + 1) + j;
					int next = current + sectors + 1;

					indices.Add(current);
					indices.Add(next);
					indices.Add(current + 1);

					indices.Add(next);
					indices.Add(next + 1);
					indices.Add(current + 1);
				}
			}

			return new OglMesh(indices.ToArray(), positions.ToArray(), normals.ToArray(), uvs.ToArray());
		}

		public static OglMesh CreateJointMesh(SkeletonUserData.Constraint constraint, SkeletonDefinitionData skeletonDef)
		{
			if (skeletonDef == null)
				return null;

			var bone1Index = skeletonDef.Bones.FindIndex(b => b.Name == constraint.Bone1);
			var bone2Index = skeletonDef.Bones.FindIndex(b => b.Name == constraint.Bone2);
			if (bone1Index < 0 || bone2Index < 0)
				return null;

			var matrices = skeletonDef.CreateBoneMatrices();
			var bone1Pos = System.Numerics.Vector3.Transform(System.Numerics.Vector3.Zero, matrices[bone1Index]);
			var bone2Pos = System.Numerics.Vector3.Transform(System.Numerics.Vector3.Zero, matrices[bone2Index]);

			var positions = new List<float>
			{
				bone1Pos.X, bone1Pos.Y, bone1Pos.Z,
				bone2Pos.X, bone2Pos.Y, bone2Pos.Z
			};
			var indices = new[] { 0, 1 };
			return new OglMesh(indices, positions.ToArray());
		}

		/// <summary>
		/// 创建胶囊体线框网格（由线条组成的网状胶囊，用于碰撞体显示）
		/// 优化：参考Unity编辑器Gizmos的胶囊碰撞体渲染方式，包含端点球体线框
		/// </summary>
		public static OglMesh CreateCapsuleWireframe(System.Numerics.Vector3 p1, System.Numerics.Vector3 p2, float radius, int circleSegments = 12)
		{
			var axis = p2 - p1;
			if (axis.Length() < 1e-6f)
				axis = new System.Numerics.Vector3(1, 0, 0);
			else
				axis = System.Numerics.Vector3.Normalize(axis);

			System.Numerics.Vector3 right, up;
			if (Math.Abs(axis.Y) < 0.9f)
			{
				up = new System.Numerics.Vector3(0, 1, 0);
				right = System.Numerics.Vector3.Normalize(System.Numerics.Vector3.Cross(axis, up));
				up = System.Numerics.Vector3.Normalize(System.Numerics.Vector3.Cross(right, axis));
			}
			else
			{
				right = new System.Numerics.Vector3(1, 0, 0);
				up = System.Numerics.Vector3.Normalize(System.Numerics.Vector3.Cross(right, axis));
				right = System.Numerics.Vector3.Normalize(System.Numerics.Vector3.Cross(axis, up));
			}

			var positions = new List<float>();
			var indices = new List<uint>();

			// 1. 端点圆环（top和bottom circles）
			uint baseIndex = 0;
			for (int i = 0; i < circleSegments; i++)
			{
				var t = (float)(2 * Math.PI * i / circleSegments);
				var offset = radius * (right * (float)Math.Cos(t) + up * (float)Math.Sin(t));
				var v1 = p1 + offset;
				var v2 = p2 + offset;
				positions.Add(v1.X); positions.Add(v1.Y); positions.Add(v1.Z);
				positions.Add(v2.X); positions.Add(v2.Y); positions.Add(v2.Z);
			}

			// 连接端点圆环的垂直线
			for (int i = 0; i < circleSegments; i++)
			{
				indices.Add(baseIndex + (uint)(i * 2)); 
				indices.Add(baseIndex + (uint)(i * 2 + 1));
			}

			// 2. 端点圆环自身的连接线（形成完整的圆）
			for (int i = 0; i < circleSegments; i++)
			{
				int j = (i + 1) % circleSegments;
				// 底部圆
				indices.Add(baseIndex + (uint)(i * 2)); 
				indices.Add(baseIndex + (uint)(j * 2));
				// 顶部圆
				indices.Add(baseIndex + (uint)(i * 2 + 1)); 
				indices.Add(baseIndex + (uint)(j * 2 + 1));
			}

			// 3. 端点球体线框（hemisphere wireframe）- Unity Gizmos风格
			// 在p1和p2处创建半球线框以显示胶囊的端点
			int hemisphereSegments = Math.Max(circleSegments / 2, 4);
			uint hemisphereBaseIndex = baseIndex + (uint)(circleSegments * 2);

			// p1端的半球：球心在p1，朝向轴的负方向（远离p2，朝向外侧）
			AddHemisphereWireframe(positions, indices, p1, axis, right, up, radius, hemisphereSegments, hemisphereBaseIndex, false);
			hemisphereBaseIndex += (uint)((hemisphereSegments + 1) * (hemisphereSegments + 1) + 1);

			// p2端的半球：球心在p2，朝向轴的正方向（远离p1，朝向外侧）
			AddHemisphereWireframe(positions, indices, p2, axis, right, up, radius, hemisphereSegments, hemisphereBaseIndex, true);

			if (positions.Count == 0) return null;
			return new OglMesh(indices.Select(i => (int)i).ToArray(), positions.ToArray());
		}

		/// <summary>
		/// 添加半球线框（用于胶囊体端点的球体显示）
		/// 半球球心在center，沿axis正方向（forward=true）或负方向（forward=false）凸起
		/// </summary>
		private static void AddHemisphereWireframe(
			List<float> positions, 
			List<uint> indices, 
			System.Numerics.Vector3 center, 
			System.Numerics.Vector3 axis, 
			System.Numerics.Vector3 right, 
			System.Numerics.Vector3 up, 
			float radius, 
			int segments,
			uint baseIndex,
			bool forward)
		{
			// 半球的轴方向：forward为true时朝axis正方向，false时朝axis负方向
			var hemisphereAxis = forward ? axis : -axis;

			// 创建纬线圈（从赤道到极点）
			int rings = segments;
			for (int ring = 0; ring <= rings; ring++)
			{
				// phi: 0 = 赤道, PI/2 = 极点
				float phi = (float)(Math.PI * ring / (2 * rings));
				float sinPhi = (float)Math.Sin(phi);
				float cosPhi = (float)Math.Cos(phi);

				for (int j = 0; j <= segments; j++)
				{
					float theta = (float)(2 * Math.PI * j / segments);
					float cosTheta = (float)Math.Cos(theta);
					float sinTheta = (float)Math.Sin(theta);

					// 在局部坐标系中计算球面点：X=right, Y=hemisphereAxis, Z=up
					var localPos = new System.Numerics.Vector3(
						cosTheta * sinPhi * radius,
						cosPhi * radius,
						sinTheta * sinPhi * radius
					);

					// 转换到世界坐标：X->right, Y->axis, Z->up
					var worldPos = center + 
						right * localPos.X + 
						hemisphereAxis * localPos.Y + 
						up * localPos.Z;

					positions.Add(worldPos.X);
					positions.Add(worldPos.Y);
					positions.Add(worldPos.Z);
				}
			}

			// 连接纬线圈的顶点（形成经线）
			int totalRings = rings + 1;
			for (int ring = 0; ring < totalRings - 1; ring++)
			{
				for (int j = 0; j <= segments; j++)
				{
					uint current = baseIndex + (uint)(ring * (segments + 1) + j);
					uint next = baseIndex + (uint)((ring + 1) * (segments + 1) + j);
					indices.Add(current);
					indices.Add(next);
				}
			}

			// 添加中心点（半球的底部中心）
			uint centerIdx = baseIndex + (uint)((rings + 1) * (segments + 1));
			positions.Add(center.X); positions.Add(center.Y); positions.Add(center.Z);

			// 从中心点到赤道（第一个环）的连线
			for (int j = 0; j <= segments; j++)
			{
				indices.Add(centerIdx);
				indices.Add(baseIndex + (uint)j);
			}
		}

		/// <summary>
		/// 创建关节轴指示器线框（三轴 XYZ，用于关节显示）
		/// </summary>
		public static OglMesh CreateJointAxisMesh(System.Numerics.Vector3 position, System.Numerics.Quaternion rotation, float size = 0.08f)
		{
			var ox = position + System.Numerics.Vector3.Transform(new System.Numerics.Vector3(size, 0, 0), rotation);
			var oy = position + System.Numerics.Vector3.Transform(new System.Numerics.Vector3(0, size, 0), rotation);
			var oz = position + System.Numerics.Vector3.Transform(new System.Numerics.Vector3(0, 0, size), rotation);
			var positions = new List<float>
			{
				position.X, position.Y, position.Z,
				ox.X, ox.Y, ox.Z,
				position.X, position.Y, position.Z,
				oy.X, oy.Y, oy.Z,
				position.X, position.Y, position.Z,
				oz.X, oz.Y, oz.Z
			};
			var indices = new List<uint> { 0, 1, 2, 3, 4, 5 };
			return new OglMesh(indices.Select(i => (int)i).ToArray(), positions.ToArray());
		}
	}
}