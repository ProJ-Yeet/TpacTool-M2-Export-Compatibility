using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using TpacTool.Properties;

namespace TpacTool
{
	public partial class GalleryWindow : Window
	{
		private GalleryViewModel ViewModel => DataContext as GalleryViewModel;

		public GalleryWindow(GalleryViewModel viewModel)
		{
			DataContext = viewModel;
			InitializeComponent();
			FitToScreen();
			viewModel.PropertyChanged += (sender, args) =>
			{
				if (args.PropertyName == nameof(GalleryViewModel.TileWidth))
					UpdateColumns();
			};
			Closing += (sender, args) => SaveBounds();
			Closed += (sender, args) => viewModel.Cleanup();
		}

		/// <summary>
		/// Restores the last size / position when it is still on screen, otherwise opens at a size that
		/// fits the work area (taskbar excluded) of the primary screen, centered. All in WPF units, so
		/// display scaling is taken into account.
		/// </summary>
		private void FitToScreen()
		{
			var work = SystemParameters.WorkArea;
			var saved = ParseBounds(Settings.Default.GalleryWindowBounds);
			if (saved.HasValue && saved.Value.Width >= MinWidth && saved.Value.Height >= MinHeight &&
			    saved.Value.Left >= SystemParameters.VirtualScreenLeft - 8 &&
			    saved.Value.Top >= SystemParameters.VirtualScreenTop - 8 &&
			    saved.Value.Right <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth + 8 &&
			    saved.Value.Bottom <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight + 8)
			{
				WindowStartupLocation = WindowStartupLocation.Manual;
				Left = saved.Value.Left;
				Top = saved.Value.Top;
				Width = saved.Value.Width;
				Height = saved.Value.Height;
				return;
			}

			Width = Math.Max(Math.Min(MinWidth, work.Width), Math.Min(1240, work.Width * 0.9));
			Height = Math.Max(Math.Min(MinHeight, work.Height), Math.Min(800, work.Height * 0.9));
			WindowStartupLocation = WindowStartupLocation.Manual;
			Left = work.Left + (work.Width - Width) / 2;
			Top = work.Top + (work.Height - Height) / 2;
		}

		private void SaveBounds()
		{
			var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
			if (bounds.IsEmpty)
				return;
			Settings.Default.GalleryWindowBounds = string.Join(",",
				new[] { bounds.Left, bounds.Top, bounds.Width, bounds.Height }
					.Select(v => v.ToString("0", CultureInfo.InvariantCulture)));
			Settings.Default.Save();
		}

		private static Rect? ParseBounds(string text)
		{
			if (string.IsNullOrWhiteSpace(text))
				return null;
			var parts = text.Split(',');
			if (parts.Length != 4)
				return null;
			var values = new double[4];
			for (int i = 0; i < 4; i++)
				if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
					return null;
			return new Rect(values[0], values[1], values[2], values[3]);
		}

		private void Gallery_SizeChanged(object sender, SizeChangedEventArgs e)
		{
			UpdateColumns();
		}

		private void UpdateColumns()
		{
			var vm = ViewModel;
			if (vm == null)
				return;
			// padding + vertical scroll bar
			var available = Gallery.ActualWidth - Gallery.Padding.Left - Gallery.Padding.Right -
			                SystemParameters.VerticalScrollBarWidth - 2;
			// a tile is TileWidth wide plus its 3px margin on each side
			vm.Columns = Math.Max(1, (int) (available / (vm.TileWidth + 6)));
		}

		private void Tile_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
		{
			if (!(sender is FrameworkElement element) || !(element.DataContext is GalleryItem item))
				return;
			if (e.ClickCount == 2)
			{
				// the first click of the double click already toggled the tile: undo that
				item.IsSelected = !item.IsSelected;
				ViewModel?.OpenInMainWindow(item);
			}
			else
			{
				ViewModel?.OnTileClicked(item, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
			}
			e.Handled = true;
		}
	}
}
