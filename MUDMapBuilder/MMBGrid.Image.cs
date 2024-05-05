﻿using AbarimMUD.Data;
using GoRogue;
using GoRogue.MapViews;
using GoRogue.Pathing;
using SkiaSharp;
using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Numerics;
using Direction = AbarimMUD.Data.Direction;
using Rectangle = System.Drawing.Rectangle;

namespace MUDMapBuilder
{
	partial class MMBGrid
	{
		private const int RoomHeight = 32;
		private const int TextPadding = 8;
		private static readonly Point RoomSpace = new Point(32, 32);
		private int[] _cellsWidths;


		public byte[] BuildPng()
		{
			byte[] imageBytes = null;
			using (SKPaint paint = new SKPaint())
			{
				paint.Color = SKColors.Black;
				paint.IsAntialias = true;
				paint.Style = SKPaintStyle.Stroke;
				paint.TextAlign = SKTextAlign.Center;

				// First grid run - determine cells width
				_cellsWidths = new int[Width];
				for (var x = 0; x < Width; ++x)
				{
					for (var y = 0; y < Height; ++y)
					{
						var room = this[x, y];
						if (room == null)
						{
							continue;
						}

						var sz = (int)(paint.MeasureText(room.Room.Name) + TextPadding * 2 + 0.5f);
						if (sz > _cellsWidths[x])
						{
							_cellsWidths[x] = sz;
						}
					}
				}


				// Second run - draw the map
				var imageWidth = 0;
				for (var i = 0; i < _cellsWidths.Length; ++i)
				{
					imageWidth += _cellsWidths[i];
				}

				imageWidth += (Width + 1) * RoomSpace.X;

				SKImageInfo imageInfo = new SKImageInfo(imageWidth,
														Height * RoomHeight + (Height + 1) * RoomSpace.Y);


				using (SKSurface surface = SKSurface.Create(imageInfo))
				{
					SKCanvas canvas = surface.Canvas;

					for (var x = 0; x < Width; ++x)
					{
						for (var y = 0; y < Height; ++y)
						{
							var mMBRoom = this[x, y];
							if (mMBRoom == null)
							{
								continue;
							}

							// Draw room
							var rect = GetRoomRect(new Point(x, y));
							paint.StrokeWidth = 2;
							canvas.DrawRect(rect.X, rect.Y, _cellsWidths[x], RoomHeight, paint);

							// Draw connections
							foreach (var roomExit in mMBRoom.Room.Exits)
							{
								if (roomExit.TargetRoom == null)
								{
									continue;
								}

								var targetRoom = GetByRoom(roomExit.TargetRoom);
								if (targetRoom == null)
								{
									continue;
								}

								var targetPos = targetRoom.Position;
								if (AreRoomsConnected(new Point(x, y), targetPos, roomExit.Direction))
								{
									// Connection is drawn already
									continue;
								}

								// Check if the straight connection could be drawn
								var straightConnection = false;

								Point? startCheck = null;
								Point? endCheck = null;

								switch (roomExit.Direction)
								{
									case Direction.North:
										if (y - targetPos.Y == 1)
										{
											straightConnection = true;
										}
										else if (y - targetPos.Y > 1)
										{
											startCheck = new Point(x, y - 1);
											endCheck = new Point(targetPos.X, targetPos.Y + 1);
										}
										break;
									case Direction.East:
										if (targetPos.X - x == 1)
										{
											straightConnection = true;
										}
										else if (targetPos.X - x > 1)
										{
											startCheck = new Point(x + 1, y);
											endCheck = new Point(targetPos.X - 1, targetPos.Y);
										}
										break;
									case Direction.South:
										if (targetPos.Y - y == 1)
										{
											straightConnection = true;
										}
										else if (targetPos.Y - 1 > 1)
										{
											startCheck = new Point(x, y + 1);
											endCheck = new Point(targetPos.X, targetPos.Y - 1);
										}
										break;
									case Direction.West:
										if (x - targetPos.X == 1)
										{
											straightConnection = true;
										}
										else if (x - targetPos.X > 1)
										{
											startCheck = new Point(x + 1, y);
											endCheck = new Point(targetPos.X - 1, targetPos.Y);
										}
										break;
									case Direction.Up:
										if (targetPos.X - x == 1)
										{
											straightConnection = true;
										}
										else if (y - targetPos.Y >= 1 && targetPos.X - x >= 1)
										{
											startCheck = new Point(x + 1, y - 1);
											endCheck = new Point(targetPos.X - 1, targetPos.Y + 1);
										}
										break;
									case Direction.Down:
										if (x - targetPos.X == 1)
										{
											straightConnection = true;
										}
										else if (targetPos.Y - y >= 1 && x - targetPos.X >= 1)
										{
											startCheck = new Point(x - 1, y + 1);
											endCheck = new Point(targetPos.X + 1, targetPos.Y - 1);
										}
										break;
								}

								if (startCheck != null && endCheck != null)
								{
									straightConnection = true;
									for (var checkX = Math.Min(startCheck.Value.X, endCheck.Value.X); checkX <= Math.Max(startCheck.Value.X, endCheck.Value.X); ++checkX)
									{
										for (var checkY = Math.Min(startCheck.Value.Y, endCheck.Value.Y); checkY <= Math.Max(startCheck.Value.Y, endCheck.Value.Y); ++checkY)
										{
											if (this[checkX, checkY] != null)
											{
												straightConnection = false;
												goto finishCheck;
											}
										}
									}

								finishCheck:;
								}

								if (straightConnection)
								{
									// Source and target room are close to each other, hence draw the simple line
									var targetRect = GetRoomRect(targetPos);
									var sourceScreen = GetConnectionPoint(rect, roomExit.Direction);
									var targetScreen = GetConnectionPoint(targetRect, roomExit.Direction.GetOppositeDirection());
									canvas.DrawLine(sourceScreen.X, sourceScreen.Y, targetScreen.X, targetScreen.Y, paint);
								}
								else
								{
									// In other case we might have to use A* to draw the path
									// Basic idea is to consider every cell(spaces between rooms are cells too) as grid 2x2
									// Where 1 means center
									var aStarSourceCoords = new Point(x, y).ToAStarCoord() + roomExit.Direction.ToAStarCoord();
									var aStarTargetCoords = targetPos.ToAStarCoord() + roomExit.Direction.GetOppositeDirection().ToAStarCoord();

									var aStarView = new AStarView(this);
									var pathFinder = new AStar(aStarView, Distance.MANHATTAN);
									var path = pathFinder.ShortestPath(aStarSourceCoords, aStarTargetCoords);
									var steps = path.Steps.ToArray();

									var src = aStarSourceCoords;
									for (var i = 0; i < steps.Length; i++)
									{
										var dest = steps[i];

										var sourceScreen = ToScreenCoord(src);
										var targetScreen = ToScreenCoord(dest);
										canvas.DrawLine(sourceScreen.X, sourceScreen.Y, targetScreen.X, targetScreen.Y, paint);

										src = dest;
									}
								}

								mMBRoom.Connect(roomExit.Direction, targetPos);
							}

							paint.StrokeWidth = 1;
							canvas.DrawText(mMBRoom.Room.Name, rect.X + rect.Width / 2, rect.Y + rect.Height / 2, paint);
						}
					}

					using (SKImage image = surface.Snapshot())
					using (SKData data = image.Encode(SKEncodedImageFormat.Png, 100))
					using (MemoryStream mStream = new MemoryStream(data.ToArray()))
					{
						imageBytes = data.ToArray();
					}
				}

			}

			return imageBytes;
		}

		private Rectangle GetRoomRect(Point pos)
		{
			var screenX = RoomSpace.X;
			for (var x = 0; x < pos.X; ++x)
			{
				screenX += _cellsWidths[x];
				screenX += RoomSpace.X;
			}

			return new Rectangle(screenX, pos.Y * RoomHeight + (pos.Y + 1) * RoomSpace.Y, _cellsWidths[pos.X], RoomHeight);
		}

		private Point ToScreenCoord(Coord coord)
		{
			// Shift by initial space
			coord -= new Coord(2, 2);

			// Determine grid coord
			var gridCoord = new Point(coord.X / 4, coord.Y / 4);

			// Calculate cell screen coords
			var screenX = RoomSpace.X;
			for (var x = 0; x < gridCoord.X; ++x)
			{
				screenX += _cellsWidths[x];
				screenX += RoomSpace.X;
			}

			var screenY = gridCoord.Y * RoomHeight + (gridCoord.Y + 1) * RoomSpace.Y;

			switch (coord.X % 4)
			{
				case 1:
					screenX += _cellsWidths[gridCoord.X] / 2;
					break;
				case 2:
					screenX += _cellsWidths[gridCoord.X];
					break;
				case 3:
					screenX += _cellsWidths[gridCoord.X] + RoomSpace.X / 2;
					break;
			}

			switch (coord.Y % 4)
			{
				case 1:
					screenY += RoomHeight / 2;
					break;
				case 2:
					screenY += RoomHeight;
					break;
				case 3:
					screenY += RoomHeight + RoomSpace.Y / 2;
					break;
			}

			return new Point(screenX, screenY);
		}

		private static Point GetConnectionPoint(Rectangle rect, Direction direction)
		{
			switch (direction)
			{
				case Direction.North:
					return new Point(rect.X + rect.Width / 2, rect.Y);
				case Direction.East:
					return new Point(rect.Right, rect.Y + rect.Height / 2);
				case Direction.South:
					return new Point(rect.X + rect.Width / 2, rect.Bottom);
				case Direction.West:
					return new Point(rect.Left, rect.Y + rect.Height / 2);
				case Direction.Up:
					return new Point(rect.Right, rect.Y);
				case Direction.Down:
					return new Point(rect.X, rect.Bottom);
			}

			throw new Exception($"Unknown direction {direction}");
		}

		private class AStarView : IMapView<bool>
		{
			private readonly MMBGrid _grid;

			public bool this[Coord pos] => CheckMove(pos.ToVector2());

			public bool this[int index1D] => throw new NotImplementedException();

			public bool this[int x, int y] => CheckMove(new Vector2(x, y));

			public int Height => _grid.Height * 4 + 2;

			public int Width => _grid.Width * 4 + 2;

			public AStarView(MMBGrid grid)
			{
				_grid = grid;
			}

			public bool CheckMove(Vector2 coord)
			{
				// Firstly determine whether wether we're at cell or space zone
				if (coord.X < 2 || coord.Y < 2)
				{
					// Space
					return true;
				}

				// Shift by initial space
				coord.X -= 2;
				coord.Y -= 2;

				var cx = coord.X % 4;
				var cy = coord.Y % 4;
				if (cx > 2 || cy > 2)
				{
					// Space
					return true;
				}

				// Cell
				var gridPoint = new Point((int)(coord.X / 4), (int)(coord.Y / 4));
				var room = _grid[gridPoint];

				return room == null;
			}
		}
	}

	internal static class MMBGridImageExtensions
	{
		public static Coord ToAStarCoord(this Direction direction)
		{
			switch (direction)
			{
				case Direction.North:
					return new Coord(1, 0);
				case Direction.East:
					return new Coord(2, 1);
				case Direction.South:
					return new Coord(1, 2);
				case Direction.West:
					return new Coord(0, 1);
				case Direction.Up:
					return new Coord(2, 0);
				case Direction.Down:
					return new Coord(0, 2);
			}

			throw new Exception($"Unknown direction {direction}");
		}

		public static Coord ToAStarCoord(this Point source) => new Coord(source.X * 4 + 2, source.Y * 4 + 2);
		public static Point ToGridPoint(this Coord source) => new Point((source.X - 2) / 4, (source.Y - 2) / 4);
		public static Vector2 ToVector2(this Coord source) => new Vector2(source.X, source.Y);
		public static Coord ToCoord(this Vector2 source) => new Coord((int)source.X, (int)source.Y);
	}
}
