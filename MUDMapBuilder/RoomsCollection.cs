﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace MUDMapBuilder
{
	public class RoomsCollection : IEnumerable<MMBRoom>
	{
		private enum ConnectionBrokenType
		{
			Normal,
			NotStraight,
			HasObstacles,
			Long
		}

		private readonly List<MMBRoom> _rooms = new List<MMBRoom>();
		private MMBGrid _grid;
		private Point _min;
		private int? _selectedRoomId;

		public int Count => _rooms.Count;

		public MMBGrid Grid
		{
			get
			{
				UpdateGrid();
				return _grid;
			}
		}

		public BrokenConnectionsInfo BrokenConnections => Grid.BrokenConnections;

		internal MMBRoom this[int index] => _rooms[index];

		public int? SelectedRoomId
		{
			get => _selectedRoomId;

			set
			{
				_selectedRoomId = value;

				if (_grid != null)
				{
					_grid.SelectedRoomId = value;
				}
			}
		}

		private void OnRoomInvalid(object sender, EventArgs e) => InvalidateGrid();

		internal void Add(MMBRoom room)
		{
			room.Invalid += OnRoomInvalid;

			_rooms.Add(room);
			InvalidateGrid();
		}

		internal void Remove(int roomId)
		{
			var room = GetRoomById(roomId);
			room.Invalid -= OnRoomInvalid;
			_rooms.Remove(room);

			InvalidateGrid();
		}

		public void InvalidateGrid()
		{
			_grid = null;
		}

		private void UpdateGrid()
		{
			if (_grid != null)
			{
				return;
			}

			var rect = CalculateRectangle();

			_min = new Point(rect.X, rect.Y);
			_grid = new MMBGrid(rect.Width, rect.Height)
			{
				SelectedRoomId = SelectedRoomId
			};

			// First run: add rooms
			foreach (var room in _rooms)
			{
				var coord = new Point(room.Position.X - rect.X, room.Position.Y - rect.Y);
				_grid[coord.X, coord.Y] = new MMBRoomCell(room.Room, coord)
				{
					MarkColor = room.MarkColor,
					ForceMark = room.ForceMark,
				};
			}

			// Second run: add connections
			foreach (var room in _rooms)
			{
				var sourceGridRoom = _grid.GetRoomById(room.Id);
				var gridStartPos = sourceGridRoom.Position;

				foreach (var pair in room.Room.Exits)
				{
					var exitDir = pair.Key;
					var exitRoom = pair.Value;
					if (exitDir == MMBDirection.Up || exitDir == MMBDirection.Down)
					{
						continue;
					}

					var targetGridRoom = _grid.GetRoomById(exitRoom.Id);
					if (targetGridRoom == null)
					{
						continue;
					}

					var gridTargetPos = targetGridRoom.Position;
					if (!IsConnectionStraight(gridStartPos, gridTargetPos, exitDir))
					{
						continue;
					}

					var delta = exitDir.GetDelta();
					for (var sourcePos = gridStartPos; sourcePos != gridTargetPos; sourcePos.X += delta.X, sourcePos.Y += delta.Y)
					{
						if (_grid[sourcePos.X, sourcePos.Y] is MMBRoomCell)
						{
							continue;
						}

						var connectionsObstacle = (MMBConnectionsCell)_grid[sourcePos];
						if (connectionsObstacle == null)
						{
							connectionsObstacle = new MMBConnectionsCell(sourcePos);
							_grid[sourcePos] = connectionsObstacle;
						}

						connectionsObstacle.AddPair(sourceGridRoom, targetGridRoom);
					}
				}
			}

			_grid.BrokenConnections = CalculateBrokenConnections();
		}

		public void ExpandGrid(Point pos, Point vec)
		{
			foreach (var room in _rooms)
			{
				var roomPos = room.Position;
				if (vec.X < 0 && roomPos.X <= pos.X)
				{
					roomPos.X += vec.X;
				}

				if (vec.X > 0 && roomPos.X >= pos.X)
				{
					roomPos.X += vec.X;
				}

				if (vec.Y < 0 && roomPos.Y <= pos.Y)
				{
					roomPos.Y += vec.Y;
				}

				if (vec.Y > 0 && roomPos.Y >= pos.Y)
				{
					roomPos.Y += vec.Y;
				}

				room.Position = roomPos;
			}
		}

		public MMBRoom GetRoomById(int id) => (from r in _rooms where r.Id == id select r).FirstOrDefault();
		public MMBRoom GetRoomByPosition(Point pos) => (from r in _rooms where r.Position == pos select r).FirstOrDefault();

		public Dictionary<int, Point> MeasurePushRoom(MMBRoom firstRoom, Point firstForceVector)
		{
			var roomsToPush = new Dictionary<int, Point>();

			var toProcess = new List<Tuple<MMBRoom, Point>>
			{
				new Tuple<MMBRoom, Point>(firstRoom, firstForceVector)
			};
			while (toProcess.Count > 0)
			{
				var item = toProcess[0];
				var room = item.Item1;
				var pos = room.Position;
				toProcess.RemoveAt(0);

				roomsToPush[room.Id] = item.Item2;

				// Process neighbour rooms
				foreach (var pair in room.Room.Exits)
				{
					var exitDir = pair.Key;
					var exitRoom = pair.Value;
					var forceVector = item.Item2;

					var targetRoom = GetRoomById(exitRoom.Id);
					if (targetRoom == null || roomsToPush.ContainsKey(exitRoom.Id))
					{
						continue;
					}

					if (!IsConnectionStraight(room.Position, targetRoom.Position, exitDir))
					{
						// Skip broken connections
						continue;
					}

					var targetPos = targetRoom.Position;
					switch (exitDir)
					{
						case MMBDirection.North:
							forceVector.Y += Math.Abs(targetPos.Y - pos.Y) - 1;
							if (forceVector.Y > 0)
							{
								forceVector.Y = 0;
							}
							break;

						case MMBDirection.East:
							forceVector.X -= Math.Abs(targetPos.X - pos.X) - 1;
							if (forceVector.X < 0)
							{
								forceVector.X = 0;
							}
							break;

						case MMBDirection.South:
							forceVector.Y -= Math.Abs(targetPos.Y - pos.Y) - 1;
							if (forceVector.Y < 0)
							{
								forceVector.Y = 0;
							}
							break;

						case MMBDirection.West:
							forceVector.X += Math.Abs(targetPos.X - pos.X) - 1;
							if (forceVector.X > 0)
							{
								forceVector.X = 0;
							}
							break;

						case MMBDirection.Up:
							forceVector.X -= Math.Abs(targetPos.X - pos.X) - 1;
							forceVector.Y += Math.Abs(targetPos.Y - pos.Y) - 1;

							if (forceVector.X < 0)
							{
								forceVector.X = 0;
							}

							if (forceVector.Y > 0)
							{
								forceVector.Y = 0;
							}
							break;

						case MMBDirection.Down:
							forceVector.X += Math.Abs(targetPos.X - pos.X) - 1;
							forceVector.Y -= Math.Abs(targetPos.Y - pos.Y) - 1;

							if (forceVector.X > 0)
							{
								forceVector.X = 0;
							}
							if (forceVector.Y < 0)
							{
								forceVector.Y = 0;
							}

							break;
					}

					if (forceVector.X != 0 || forceVector.Y != 0)
					{
						toProcess.Add(new Tuple<MMBRoom, Point>(targetRoom, forceVector));
					}
				}
			}

			return roomsToPush;
		}

		public void PushRoom(MMBRoom firstRoom, Point firstForceVector)
		{
			var roomsToPush = MeasurePushRoom(firstRoom, firstForceVector);

			// Process rooms in special order so rooms dont get placed on each other
			while (roomsToPush.Count > 0)
			{
				foreach (var pair in roomsToPush)
				{
					var room = GetRoomById(pair.Key);
					var delta = pair.Value;

					var newPos = new Point(room.Position.X + delta.X, room.Position.Y + delta.Y);

					var existingRoom = GetRoomByPosition(newPos);
					if (existingRoom != null)
					{
						if (roomsToPush.ContainsKey(existingRoom.Id))
						{
							continue;
						}

						// Place existing room on the older position
						existingRoom.Position = room.Position;
					}

					room.Position = newPos;
					roomsToPush.Remove(pair.Key);
					break;
				}
			}
		}

		public static bool IsConnectionStraight(Point sourcePos, Point targetPos, MMBDirection exitDir)
		{
			var isStraight = false;
			switch (exitDir)
			{
				case MMBDirection.North:
					isStraight = targetPos.X - sourcePos.X == 0 && targetPos.Y < sourcePos.Y;
					break;

				case MMBDirection.South:
					isStraight = targetPos.X - sourcePos.X == 0 && targetPos.Y > sourcePos.Y;
					break;

				case MMBDirection.West:
					isStraight = targetPos.X < sourcePos.X && targetPos.Y - sourcePos.Y == 0;
					break;

				case MMBDirection.East:
					isStraight = targetPos.X > sourcePos.X && targetPos.Y - sourcePos.Y == 0;
					break;

				case MMBDirection.Up:
					isStraight = targetPos.X > sourcePos.X && targetPos.Y < sourcePos.Y;
					break;

				case MMBDirection.Down:
					isStraight = targetPos.X < sourcePos.X && targetPos.Y > sourcePos.Y;
					break;
			}

			return isStraight;
		}

		private MMBCell GetCell(Point checkPos)
		{
			UpdateGrid();

			checkPos.X -= _min.X;
			checkPos.Y -= _min.Y;

			return _grid[checkPos.X, checkPos.Y];
		}

		private ConnectionBrokenType CheckConnectionBroken(MMBRoom sourceRoom, MMBRoom targetRoom, MMBDirection exitDir, out HashSet<int> obstacles)
		{
			obstacles = new HashSet<int>();
			var sourcePos = sourceRoom.Position;
			var targetPos = targetRoom.Position;

			var delta = exitDir.GetDelta();
			var desiredPos = new Point(sourcePos.X + delta.X, sourcePos.Y + delta.Y);
			if (desiredPos == targetPos)
			{
				return ConnectionBrokenType.Normal;
			}

			var isStraight = IsConnectionStraight(sourcePos, targetPos, exitDir);
			if (!isStraight)
			{
				// Even non-straight connection is considered straight
				// If there's another straight connection between same rooms
				return ConnectionBrokenType.NotStraight;
			}
			else
			{
				if (exitDir == MMBDirection.Up || exitDir == MMBDirection.Down)
				{
					return ConnectionBrokenType.Long;
				}

				// Check there are no obstacles on the path
				var startCheck = sourcePos;
				var endCheck = targetPos;
				switch (exitDir)
				{
					case MMBDirection.North:
						startCheck = new Point(sourcePos.X, sourcePos.Y - 1);
						endCheck = new Point(targetPos.X, targetPos.Y + 1);
						break;
					case MMBDirection.East:
						startCheck = new Point(sourcePos.X + 1, sourcePos.Y);
						endCheck = new Point(targetPos.X - 1, targetPos.Y);
						break;
					case MMBDirection.South:
						startCheck = new Point(sourcePos.X, sourcePos.Y + 1);
						endCheck = new Point(targetPos.X, targetPos.Y - 1);
						break;
					case MMBDirection.West:
						startCheck = new Point(sourcePos.X - 1, sourcePos.Y);
						endCheck = new Point(targetPos.X + 1, targetPos.Y);
						break;
					case MMBDirection.Up:
						startCheck = new Point(sourcePos.X + 1, sourcePos.Y - 1);
						endCheck = new Point(targetPos.X - 1, targetPos.Y + 1);
						break;
					case MMBDirection.Down:
						startCheck = new Point(sourcePos.X - 1, sourcePos.Y + 1);
						endCheck = new Point(targetPos.X + 1, targetPos.Y - 1);
						break;
				}

				for (var x = Math.Min(startCheck.X, endCheck.X); x <= Math.Max(startCheck.X, endCheck.X); ++x)
				{
					for (var y = Math.Min(startCheck.Y, endCheck.Y); y <= Math.Max(startCheck.Y, endCheck.Y); ++y)
					{
						var pos = new Point(x, y);
						if (pos == sourcePos || pos == targetPos)
						{
							continue;
						}

						var cell = GetCell(pos);
						var asRoomCell = cell as MMBRoomCell;
						if (asRoomCell != null)
						{
							obstacles.Add(asRoomCell.Room.Id);
						}

						/*					var asConnectionsCell = cell as MMBConnectionsCell;
											if (asConnectionsCell != null && asConnectionsCell.Count > 1)
											{
												noObstacles = false;
												break;
											}*/
					}
				}

				if (obstacles.Count > 0)
				{
					return ConnectionBrokenType.HasObstacles;
				}
			}

			return ConnectionBrokenType.Long;
		}

		private BrokenConnectionsInfo CalculateBrokenConnections()
		{
			var result = new BrokenConnectionsInfo();

			foreach (var room in _rooms)
			{
				var pos = room.Position;
				foreach (var pair in room.Room.Exits)
				{
					var exitDir = pair.Key;
					var exitRoom = pair.Value;

					var targetRoom = GetRoomById(exitRoom.Id);
					if (targetRoom == null)
					{
						continue;
					}

					HashSet<int> obstacles;
					var brokenType = CheckConnectionBroken(room, targetRoom, exitDir, out obstacles);
					switch (brokenType)
					{
						case ConnectionBrokenType.Normal:
							result.Normal.Add(room.Id, targetRoom.Id, exitDir);
							break;
						case ConnectionBrokenType.NotStraight:
							result.NonStraight.Add(room.Id, targetRoom.Id, exitDir);
							break;
						case ConnectionBrokenType.HasObstacles:
							var conn = result.WithObstacles.Add(room.Id, targetRoom.Id, exitDir);
							foreach (var o in obstacles)
							{
								conn.Obstacles.Add(o);
							}
							break;
						case ConnectionBrokenType.Long:
							result.Long.Add(room.Id, targetRoom.Id, exitDir);
							break;
					}
				}
			}

			var toDelete = new List<MMBConnection>();
			foreach (var vs in result.NonStraight)
			{
				if (result.Normal.Find(vs.SourceRoomId, vs.TargetRoomId) != null ||
					result.Long.Find(vs.SourceRoomId, vs.TargetRoomId) != null)
				{
					toDelete.Add(vs);
				}
			}

			foreach (var vs in toDelete)
			{
				result.NonStraight.Remove(vs);
			}

			return result;
		}

		public Rectangle CalculateRectangle()
		{
			var min = new Point();
			var max = new Point();
			var minSet = false;
			var maxSet = false;

			foreach (var room in _rooms)
			{
				if (!minSet)
				{
					min = room.Position;
					minSet = true;
				}
				else
				{
					if (room.Position.X < min.X)
					{
						min.X = room.Position.X;
					}

					if (room.Position.Y < min.Y)
					{
						min.Y = room.Position.Y;
					}
				}

				if (!maxSet)
				{
					max = room.Position;
					maxSet = true;
				}
				else
				{
					if (room.Position.X > max.X)
					{
						max.X = room.Position.X;
					}

					if (room.Position.Y > max.Y)
					{
						max.Y = room.Position.Y;
					}
				}
			}

			return new Rectangle(min.X, min.Y, max.X - min.X + 1, max.Y - min.Y + 1);
		}

		public void FixPlacementOfSingleExitRooms()
		{
			foreach (var room in _rooms)
			{
				var pos = room.Position;
				foreach (var pair in room.Room.Exits)
				{
					var exitDir = pair.Key;
					var exitRoom = pair.Value;

					var targetRoom = GetRoomById(exitRoom.Id);
					if (targetRoom == null)
					{
						continue;
					}

					if (exitRoom.Exits.Count > 1)
					{
						continue;
					}

					var delta = exitDir.GetDelta();
					var desiredPos = new Point(pos.X + delta.X, pos.Y + delta.Y);
					if (targetRoom.Position == desiredPos)
					{
						// The target room is already at desired pos
						continue;
					}

					// Check if the spot is free
					var usedByRoom = GetRoomByPosition(desiredPos);
					if (usedByRoom != null)
					{
						// Spot is occupied
						continue;
					}

					// Place target room next to source
					targetRoom.Position = desiredPos;
				}
			}

			InvalidateGrid();
		}

		public int CalculateArea() => CalculateRectangle().CalculateArea();

		public RoomsCollection Clone()
		{
			var result = new RoomsCollection();
			foreach (var r in _rooms)
			{
				result.Add(r.Clone());
			}

			return result;
		}

		public IEnumerator<MMBRoom> GetEnumerator() => _rooms.GetEnumerator();

		IEnumerator IEnumerable.GetEnumerator() => _rooms.GetEnumerator();


		public override int GetHashCode()
		{
			var result = 0;

			foreach (var room in _rooms)
			{
				result ^= room.Id;
				result ^= (room.Position.X << 8);
				result ^= (room.Position.Y << 16);
			}

			return result;
		}
	}
}