using System;
using System.IO;
using System.Linq;
using System.Windows.Media;
using GalaSoft.MvvmLight;
using TpacTool.Lib;
using Material = TpacTool.Lib.Material;

namespace TpacTool
{
	public enum GalleryAssetKind
	{
		Model,
		Texture,
		Material
	}

	/// <summary>
	/// One tile in the asset gallery.
	/// </summary>
	public sealed class GalleryItem : ObservableObject
	{
		private readonly Action<GalleryItem> _onSelectionChanged;
		private bool _isSelected;
		private volatile ImageSource _thumbnail;
		private bool _failed;

		// written under ThumbnailService's lock
		internal volatile bool Pending;

		public AssetItem Asset { get; }

		public GalleryAssetKind Kind { get; }

		public string Name { get; }

		public string Package { get; }

		public string Info { get; }

		public bool IsRigged { get; }

		public string ToolTip => $"{Name}\n{Info}\n{Package}";

		public string Placeholder => _thumbnail != null ? string.Empty : _failed ? "no preview" : "loading…";

		public bool IsSelected
		{
			get => _isSelected;
			set
			{
				if (Set(ref _isSelected, value))
					_onSelectionChanged?.Invoke(this);
			}
		}

		/// <summary>
		/// The thumbnail. Reading it while it is missing queues it for rendering, so only tiles that are
		/// actually shown get a thumbnail.
		/// </summary>
		public ImageSource Thumbnail
		{
			get
			{
				var thumb = _thumbnail;
				if (thumb == null && !_failed && !Pending)
					ThumbnailService.Request(this);
				return thumb;
			}
		}

		public GalleryItem(AssetItem asset, Action<GalleryItem> onSelectionChanged)
		{
			Asset = asset;
			Name = asset.Name;
			_onSelectionChanged = onSelectionChanged;
			Package = string.IsNullOrEmpty(asset.FilePath) ? string.Empty : Path.GetFileName(asset.FilePath);

			switch (asset)
			{
				case Metamesh metamesh:
					Kind = GalleryAssetKind.Model;
					IsRigged = metamesh.Meshes.Count > 0 && metamesh.Meshes.Any(m => m.SkinDataSize > 0);
					var lods = metamesh.Meshes.Select(m => m.Lod).Distinct().Count();
					var meshCount = metamesh.Meshes.Count(m => m.Lod == 0);
					Info = $"Model · {meshCount} mesh{(meshCount == 1 ? "" : "es")} · {lods} LOD{(lods == 1 ? "" : "s")}" +
					       (IsRigged ? " · rigged" : "");
					break;
				case Texture texture:
					Kind = GalleryAssetKind.Texture;
					Info = $"Texture · {texture.Width}×{texture.Height} · {texture.Format}" +
					       (texture.HasPixelData ? "" : " · no pixel data");
					break;
				case Material material:
					Kind = GalleryAssetKind.Material;
					Info = $"Material · {material.Textures.Count} texture{(material.Textures.Count == 1 ? "" : "s")}";
					break;
				default:
					throw new ArgumentException("Unsupported gallery asset: " + asset.GetType().Name);
			}
		}

		internal void SetThumbnail(ImageSource thumbnail, bool failed)
		{
			_thumbnail = thumbnail;
			_failed = failed;
			Pending = false;
			RaisePropertyChanged(nameof(Thumbnail));
			RaisePropertyChanged(nameof(Placeholder));
		}

		/// <summary>Forgets the thumbnail without notifying, to free memory. It is rebuilt when shown again.</summary>
		internal void DropThumbnail()
		{
			_thumbnail = null;
		}

		public bool Matches(string[] terms)
		{
			foreach (var term in terms)
			{
				if (Name.IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0 &&
				    Package.IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0)
					return false;
			}
			return true;
		}
	}
}
