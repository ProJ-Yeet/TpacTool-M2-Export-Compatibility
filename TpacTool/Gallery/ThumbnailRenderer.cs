using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using TpacTool.Lib;
using Material = TpacTool.Lib.Material;
using Mesh = TpacTool.Lib.Mesh;

namespace TpacTool
{
	/// <summary>
	/// Builds gallery thumbnails on the calling thread. Model thumbnails are rendered offscreen with
	/// WPF 3D, so this must run on an STA thread. Every returned bitmap is frozen and can be handed to
	/// the UI thread.
	/// </summary>
	internal static class ThumbnailRenderer
	{
		private const int MaxTextureSize = 256;

		public static BitmapSource Render(AssetItem asset, int size)
		{
			switch (asset)
			{
				case Metamesh metamesh:
					return RenderModel(metamesh, size);
				case Texture texture:
					return DecodeTexture(texture, MaxTextureSize, false);
				case Material material:
					return DecodeTexture(FindDiffuse(material), MaxTextureSize, false);
			}
			return null;
		}

		#region Textures

		public static Texture FindDiffuse(Material material)
		{
			if (material == null)
				return null;
			Texture fallback = null;
			foreach (var pair in material.Textures)
			{
				if (!pair.Value.TryGetItem(out var tex) || !tex.HasPixelData)
					continue;
				var name = tex.Name.ToLowerInvariant();
				if (name.EndsWith("_d") || name.EndsWith("_diffuse") || name.EndsWith("_d_4k") ||
				    name.EndsWith("_c") || name.EndsWith("_color") || name.EndsWith("_base_color") ||
				    name.EndsWith("_c_4k"))
					return tex;
				// slot 0 is the diffuse slot for almost every bannerlord shader
				if (fallback == null || pair.Key == 0)
					fallback = tex;
			}
			return fallback;
		}

		/// <summary>
		/// Decodes the smallest mipmap that is still at least <paramref name="maxSize"/> wide (or the
		/// largest one under it). Alpha is ignored unless <paramref name="keepAlpha"/> is set, since many
		/// bannerlord textures store masks in the alpha channel.
		/// </summary>
		public static BitmapSource DecodeTexture(Texture texture, int maxSize, bool keepAlpha)
		{
			if (texture == null || !texture.HasPixelData || !texture.Format.IsSupported())
				return null;
			var pixels = texture.TexturePixels.Data;
			int width = (int) texture.Width;
			int height = (int) texture.Height;
			byte[] data = pixels.PrimaryRawImage;
			var mips = pixels.RawImage != null && pixels.RawImage.Length > 0 ? pixels.RawImage[0] : null;
			if (mips != null && mips.Length > 1)
			{
				int level = 0;
				while (level + 1 < mips.Length &&
				       Math.Max(width >> level, height >> level) > maxSize &&
				       Math.Min(width >> (level + 1), height >> (level + 1)) >= 4)
				{
					level++;
				}
				data = mips[level];
				width = Math.Max(1, width >> level);
				height = Math.Max(1, height >> level);
			}
			if (data == null || width <= 0 || height <= 0)
				return null;

			int stride = width * 4;
			var buffer = new int[width * height];
			var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
			try
			{
				var writer = new TextureUtil.ARGB32Writer(handle.AddrOfPinnedObject(), width, height, stride);
				TextureUtil.DecodeTextureDataToWriter(data, width, height, texture.Format, writer, true);
			}
			finally
			{
				handle.Free();
			}

			var format = keepAlpha
				? (texture.Format.IsPremultiplied() ? PixelFormats.Pbgra32 : PixelFormats.Bgra32)
				: PixelFormats.Bgr32;
			var bitmap = BitmapSource.Create(width, height, 96, 96, format, null, buffer, stride);
			bitmap.Freeze();
			return bitmap;
		}

		#endregion

		#region Models

		private sealed class MeshGeometry
		{
			public Point3DCollection Positions = new Point3DCollection();
			public Vector3DCollection Normals = new Vector3DCollection();
			public PointCollection Uvs = new PointCollection();
			public Int32Collection Indices = new Int32Collection();
			public Material Material;
		}

		public static BitmapSource RenderModel(Metamesh metamesh, int size)
		{
			if (metamesh.Meshes.Count == 0)
				return null;
			var minLod = metamesh.Meshes.Min(m => m.Lod);
			var meshes = metamesh.Meshes.Where(m => m.Lod == minLod).ToList();

			var geometries = new List<MeshGeometry>();
			foreach (var mesh in meshes)
			{
				var geometry = ReadGeometry(mesh);
				if (geometry != null && geometry.Indices.Count >= 3)
					geometries.Add(geometry);
			}
			if (geometries.Count == 0)
				return null;

			// bounding sphere (in wpf space)
			double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
			double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
			foreach (var g in geometries)
			{
				foreach (var p in g.Positions)
				{
					minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
					minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
					minZ = Math.Min(minZ, p.Z); maxZ = Math.Max(maxZ, p.Z);
				}
			}
			var center = new Point3D((minX + maxX) / 2, (minY + maxY) / 2, (minZ + maxZ) / 2);
			double radius = 0;
			foreach (var g in geometries)
				foreach (var p in g.Positions)
					radius = Math.Max(radius, (p - center).LengthSquared);
			radius = Math.Sqrt(radius);
			if (radius <= 1e-6)
				radius = 1;

			var group = new Model3DGroup();
			group.Children.Add(new AmbientLight(Color.FromRgb(0x70, 0x70, 0x70)));
			group.Children.Add(new DirectionalLight(Color.FromRgb(0xD0, 0xD0, 0xD0), new Vector3D(0.4, -0.6, 1)));
			group.Children.Add(new DirectionalLight(Color.FromRgb(0x40, 0x40, 0x48), new Vector3D(-0.6, 0.2, -1)));

			var brushCache = new Dictionary<Material, Brush>();
			foreach (var g in geometries)
			{
				var meshGeometry = new MeshGeometry3D
				{
					Positions = g.Positions,
					TriangleIndices = g.Indices,
					TextureCoordinates = g.Uvs
				};
				if (g.Normals.Count == g.Positions.Count)
					meshGeometry.Normals = g.Normals;

				var brush = GetBrush(g.Material, brushCache);
				var material = new DiffuseMaterial(brush);
				group.Children.Add(new GeometryModel3D(meshGeometry, material) { BackMaterial = material });
			}

			// bannerlord is z-up and characters face +y (their left hand is on -x): view from the front
			// (+y), slightly to the side and above
			const double fov = 32;
			var viewDir = new Vector3D(-0.45, 0.35, -1.0); // wpf space (x, z, -y)
			viewDir.Normalize();
			var distance = radius / Math.Sin(fov * Math.PI / 360) * 1.02;
			var camera = new PerspectiveCamera(center + viewDir * distance, -viewDir, new Vector3D(0, 1, 0), fov)
			{
				NearPlaneDistance = Math.Max(distance - radius * 2, distance * 0.01),
				FarPlaneDistance = distance + radius * 2
			};

			var viewport = new Viewport3D { Width = size, Height = size, Camera = camera, ClipToBounds = true };
			viewport.Children.Add(new ModelVisual3D { Content = group });
			viewport.Measure(new Size(size, size));
			viewport.Arrange(new Rect(0, 0, size, size));
			viewport.UpdateLayout();

			var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
			bitmap.Render(viewport);
			bitmap.Freeze();
			return bitmap;
		}

		private static Brush GetBrush(Material material, Dictionary<Material, Brush> cache)
		{
			if (material != null && cache.TryGetValue(material, out var cached))
				return cached;

			Brush brush = null;
			try
			{
				var texture = DecodeTexture(FindDiffuse(material), 256, false);
				if (texture != null)
				{
					brush = new ImageBrush(texture)
					{
						ViewportUnits = BrushMappingMode.Absolute,
						Viewport = new Rect(0, 0, 1, 1),
						TileMode = TileMode.Tile,
						Stretch = Stretch.Fill
					};
				}
			}
			catch (Exception)
			{
				brush = null;
			}

			if (brush == null)
				brush = new SolidColorBrush(Color.FromRgb(0xB8, 0xB8, 0xB8));
			brush.Freeze();
			if (material != null)
				cache[material] = brush;
			return brush;
		}

		private static MeshGeometry ReadGeometry(Mesh mesh)
		{
			var result = new MeshGeometry();
			mesh.Material.TryGetItem(out result.Material);

			var stream = mesh.VertexStream?.Data;
			if (stream?.Positions != null && stream.Indices != null && stream.Positions.Length > 0)
			{
				foreach (var p in stream.Positions)
					result.Positions.Add(new Point3D(p.X, p.Z, -p.Y));
				if (stream.Normals != null && stream.Normals.Length == stream.Positions.Length)
					foreach (var n in stream.Normals)
						result.Normals.Add(new Vector3D(n.X, n.Z, -n.Y));
				if (stream.Uv1 != null && stream.Uv1.Length == stream.Positions.Length)
					foreach (var uv in stream.Uv1)
						result.Uvs.Add(new Point(uv.X, uv.Y));
				foreach (var index in stream.Indices)
					result.Indices.Add(index);
				return result;
			}

			var edit = mesh.EditData?.Data;
			if (edit?.Positions == null || edit.Vertices == null || edit.Faces == null)
				return null;
			foreach (var vertex in edit.Vertices)
			{
				var p = edit.Positions[vertex.PositionIndex];
				result.Positions.Add(new Point3D(p.X, p.Z, -p.Y));
				result.Normals.Add(new Vector3D(vertex.Normal.X, vertex.Normal.Z, -vertex.Normal.Y));
				result.Uvs.Add(new Point(vertex.Uv.X, vertex.Uv.Y));
			}
			foreach (var face in edit.Faces)
			{
				result.Indices.Add(face.V0);
				result.Indices.Add(face.V1);
				result.Indices.Add(face.V2);
			}
			return result;
		}

		#endregion
	}
}
