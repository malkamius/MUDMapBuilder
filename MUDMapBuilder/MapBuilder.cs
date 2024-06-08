﻿using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;

namespace MUDMapBuilder
{
	public class MapBuilder
	{
		private enum PositioningType
		{
			Straight,
			Obstacles,
			NonStraight,
			CompletelyIncorrect
		}

		private enum StraightenRoomResult
		{
			Success,
			Fail,
			OutOfSteps
		}

		private class StraightenConnectionResult
		{
			public MMBArea NewArea { get; private set; }
			public Point Delta { get; private set; }
			public int RoomsRemoved { get; private set; }
			public BrokenConnectionsInfo BrokenConnections { get; private set; }

			public StraightenConnectionResult(MMBArea newArea, Point delta, int roomsRemoved, BrokenConnectionsInfo brokenConnections)
			{
				NewArea = newArea;
				Delta = delta;
				RoomsRemoved = roomsRemoved;
				BrokenConnections = brokenConnections;
			}
		}

		private ResultType _resultType = ResultType.Success;
		private readonly MMBProject _project;
		private readonly IdQueue _roomsQueue = new IdQueue();
		private readonly HashSet<int> _removedRooms = new HashSet<int>();
		private readonly List<MMBArea> _history = new List<MMBArea>();
		private readonly Action<string> _log;

		private MMBArea Area => _project.Area;
		private BuildOptions Options => _project.BuildOptions;

		private MapBuilder(MMBProject project, Action<string> log)
		{
			_project = project;
			_log = log;
		}

		private void Log(string message)
		{
			if (_log == null)
			{
				return;
			}

			_log(message);
		}

		private static Point CalculateDesiredPosition(Point sourcePos, Point targetPos, MMBDirection direction)
		{
			var desiredPos = new Point();
			switch (direction)
			{
				case MMBDirection.North:
					desiredPos = new Point(sourcePos.X, targetPos.Y < sourcePos.Y ? targetPos.Y : sourcePos.Y - 1);
					break;
				case MMBDirection.South:
					desiredPos = new Point(sourcePos.X, targetPos.Y > sourcePos.Y ? targetPos.Y : sourcePos.Y + 1);
					break;
				case MMBDirection.East:
					desiredPos = new Point(targetPos.X > sourcePos.X ? targetPos.X : sourcePos.X + 1, sourcePos.Y);
					break;
				case MMBDirection.West:
					desiredPos = new Point(targetPos.X < sourcePos.X ? targetPos.X : sourcePos.X - 1, sourcePos.Y);
					break;
				case MMBDirection.Up:
					desiredPos = new Point(targetPos.X > sourcePos.X ? targetPos.X : sourcePos.X + 1, targetPos.Y < sourcePos.Y ? targetPos.Y : sourcePos.Y - 1);
					break;
				case MMBDirection.Down:
					desiredPos = new Point(targetPos.X < sourcePos.X ? targetPos.X : sourcePos.X - 1, targetPos.Y > sourcePos.Y ? targetPos.Y : sourcePos.Y + 1);
					break;
				default:
					throw new Exception($"Unknown direction {direction}");
			}

			return desiredPos;
		}

		private bool AddRunStep()
		{
			_history.Add(Area.Clone());

			if (Area.Width > Area.Count * 2 || Area.Height > Area.Count * 2)
			{
				_resultType = ResultType.MapTooBig;
				return false;
			}

			if(Options.MaxSteps <= _history.Count)
			{
				_resultType = ResultType.OutOfSteps;
				return false;
			}

			return true;
		}

		private bool PushRoom(MMBArea rooms, int firstRoomId, Point firstForceVector, bool measureRun, out int roomsRemoved)
		{
			var measure = rooms.MeasurePushRoom(firstRoomId, firstForceVector);

			roomsRemoved = measure.DeletedRooms.Length;

			if (!measureRun)
			{
				Log($"Step: {_history.Count}. Grid Size: {Area.Width}x{Area.Height}. Push room {Area.GetRoomById(firstRoomId)} to {firstForceVector}");
				if (measure.DeletedRooms.Length > 0)
				{
					if (!RemoveRooms(measure.DeletedRooms, false))
					{
						return false;
					}
				}

				// Mark for movement
				foreach (var m in measure.MovedRooms)
				{
					var room = m.Room;
					room.MarkColor = SKColors.YellowGreen;
					room.ForceMark = m.Delta;
				}

				if (!AddRunStep())
				{
					return false;
				}

				// Do the movement
				foreach (var m in measure.MovedRooms)
				{
					var room = m.Room;
					room.MarkColor = null;
					room.ForceMark = null;

					var delta = m.Delta;
					var newPos = new Point(room.Position.Value.X + delta.X, room.Position.Value.Y + delta.Y);
					room.Position = newPos;
				}

				if (!AddRunStep())
				{
					return false;
				}
			}
			else
			{
				// Remove
				foreach (var room in measure.DeletedRooms)
				{
					room.Position = null;
				}

				// Move
				foreach (var m in measure.MovedRooms)
				{
					var room = m.Room;
					var delta = m.Delta;

					var newPos = new Point(room.Position.Value.X + delta.X, room.Position.Value.Y + delta.Y);
					room.Position = newPos;
				}
			}

			return true;
		}

		private StraightenConnectionResult TryStraightenConnection(int sourceRoomId, int targetRoomId, MMBDirection direction)
		{
			var rooms = Area.Clone();

			var sourceRoom = rooms.GetRoomById(sourceRoomId);
			var targetRoom = rooms.GetRoomById(targetRoomId);
			var sourcePos = sourceRoom.Position.Value;
			var targetPos = targetRoom.Position.Value;
			var desiredPos = CalculateDesiredPosition(sourcePos, targetPos, direction);
			var delta = new Point(desiredPos.X - targetPos.X, desiredPos.Y - targetPos.Y);

			int roomsRemoved;
			PushRoom(rooms, targetRoomId, delta, true, out roomsRemoved);

			var vc = rooms.BrokenConnections;
			return new StraightenConnectionResult(rooms, delta, roomsRemoved, vc);
		}

		private int[] BuildRemoveList(int[] toRemove)
		{
			// Remove rooms from the clone in order to see how the map is split
			var rooms = Area.Clone();
			foreach (var id in toRemove)
			{
				var room = rooms.GetRoomById(id);
				room.Position = null;
			}

			// Check what parts the map was split on
			var sortedParts = rooms.GroupPositionedRooms();

			var roomsToRemove = new List<int>();
			roomsToRemove.AddRange(toRemove);

			// Add all parts except the last one with size below 10
			for (var i = 0; i < sortedParts.Length - 1; ++i)
			{
				var p = sortedParts[i];
				if (p.Count >= 10)
				{
					continue;
				}

				foreach (var id in p)
				{
					roomsToRemove.Add(id);
				}
			}

			return roomsToRemove.ToArray();
		}

		private bool RemoveRooms(MMBRoom[] toRemove, bool addAjancent = true)
		{
			MMBRoom[] roomsToRemove;

			if (addAjancent)
			{
				var idsToRemove = BuildRemoveList((from r in toRemove select r.Id).ToArray());
				roomsToRemove = (from id in idsToRemove select Area.GetRoomById(id)).ToArray();
			}
			else
			{
				roomsToRemove = toRemove;
			}

			// Mark
			foreach (var room in roomsToRemove)
			{
				room.MarkColor = SKColors.Red;
			}

			if (!AddRunStep())
			{
				return false;
			}

			// Remove
			foreach (var room in roomsToRemove)
			{
				// Remove
				room.MarkColor = null;
				room.Position = null;
				_roomsQueue.Remove(room.Id);
				_removedRooms.Add(room.Id);
			}

			return AddRunStep();
		}

		private bool ExistsInHistory(MMBArea area)
		{
			for (var i = 0; i < _history.Count; ++i)
			{
				var h = _history[i];
				if (MMBArea.AreEqual(area, h))
				{
					return true;
				}
			}

			return false;
		}

		private StraightenRoomResult StraightenConnection(MMBRoom room1, MMBRoom room2, MMBDirection direction)
		{
			// Try to move room2
			var vc = Area.BrokenConnections;
			var result1 = TryStraightenConnection(room1.Id, room2.Id, direction);
			var result2 = TryStraightenConnection(room2.Id, room1.Id, direction.GetOppositeDirection());

			var vc1 = result1.BrokenConnections;
			var vc2 = result2.BrokenConnections;
			if (vc.NonStraight.Count - vc1.NonStraight.Count <= 0 &&
				vc.NonStraight.Count - vc2.NonStraight.Count <= 0)
			{
				// No connection had been fixed
				return StraightenRoomResult.Fail;
			}

			var existInHistory2 = ExistsInHistory(result1.NewArea);
			var existInHistory1 = ExistsInHistory(result2.NewArea);

			if (existInHistory2 && existInHistory1)
			{
				return StraightenRoomResult.Fail;
			}

			// Determine which room of two to move
			bool? moveSecond = null;

			// First criteria - doesnt exist in history
			if (existInHistory1)
			{
				moveSecond = true;
			}
			else
			if (existInHistory2)
			{
				moveSecond = false;
			}

			if (moveSecond == null)
			{
				// Second criteria - amount of non-obstacle connections fixed
				if (vc1.WithObstacles.Count < vc2.WithObstacles.Count)
				{
					moveSecond = true;
				}
				else if (vc1.WithObstacles.Count > vc2.WithObstacles.Count)
				{
					moveSecond = false;
				}
			}

			if (moveSecond == null)
			{
				// Third criteria - amount of non-straight connections fixed
				if (vc1.NonStraight.Count < vc2.NonStraight.Count)
				{
					moveSecond = true;
				}
				else if (vc1.NonStraight.Count > vc2.NonStraight.Count)
				{
					moveSecond = false;
				}
			}

			if (moveSecond == null)
			{
				// Fourth criteria - amount of removed rooms
				moveSecond = result1.RoomsRemoved < result2.RoomsRemoved;
			}

			// Do the actual move
			int roomsRemoved;
			if (moveSecond.Value)
			{
				// Move room2
				if (!PushRoom(Area, room2.Id, result1.Delta, false, out roomsRemoved))
				{
					return StraightenRoomResult.OutOfSteps;
				}

				return StraightenRoomResult.Success;
			}

			// Move room1
			if (!PushRoom(Area, room1.Id, result2.Delta, false, out roomsRemoved))
			{
				return StraightenRoomResult.OutOfSteps;
			}

			return StraightenRoomResult.Success;
		}

		private bool CompactRun(MMBDirection pushDirection)
		{
			Log($"Compacting map to the {pushDirection}. Processed {Area.PositionedRoomsCount}/{Area.Count} rooms. Step {_history.Count}. Grid Size: {Area.Width}x{Area.Height}");

			// Firstly collect rooms
			var roomsToPush = new List<MMBRoom>();
			if (pushDirection == MMBDirection.West || pushDirection == MMBDirection.East)
			{
				for (var y = 0; y < Area.Height; ++y)
				{
					if (pushDirection == MMBDirection.West)
					{
						for (var x = Area.Width - 1; x >= 0; --x)
						{
							var room = Area.GetRoomByZeroBasedPosition(x, y);
							if (room != null)
							{
								roomsToPush.Add(room);
								break;
							}
						}
					}
					else
					{
						for (var x = 0; x < Area.Width; ++x)
						{
							var room = Area.GetRoomByZeroBasedPosition(x, y);
							if (room != null)
							{
								roomsToPush.Add(room);
								break;
							}
						}
					}
				}
			}
			else
			{
				for (var x = 0; x < Area.Width; ++x)
				{
					if (pushDirection == MMBDirection.North)
					{
						for (var y = Area.Height - 1; y >= 0; --y)
						{
							var room = Area.GetRoomByZeroBasedPosition(x, y);
							if (room != null)
							{
								roomsToPush.Add(room);
								break;
							}
						}
					}
					else
					{
						for (var y = 0; y < Area.Height; ++y)
						{
							var room = Area.GetRoomByZeroBasedPosition(x, y);
							if (room != null)
							{
								roomsToPush.Add(room);
								break;
							}
						}
					}
				}
			}

			// Now try to push every room in the provided direction until it's possible
			foreach (var room in roomsToPush)
			{
				var continueToPush = true;
				while (continueToPush)
				{
					continueToPush = false;
					var measure = Area.MeasureCompactPushRoom(room.Id, pushDirection);
					if (measure.DeletedRooms.Length > 0)
					{
						// Should never happen
						Debug.Assert(false);
					}
					else
					{
						// Test push
						var vc = Area.BrokenConnections;
						var rooms = Area.Clone();

						foreach (var m in measure.MovedRooms)
						{
							var newPos = new Point(m.Room.Position.Value.X + m.Delta.X,
								m.Room.Position.Value.Y + m.Delta.Y);

							var roomClone = rooms.GetRoomById(m.Room.Id);
							roomClone.Position = newPos;
						}

						var vc2 = rooms.BrokenConnections;
						if (vc2.WithObstacles.Count > vc.WithObstacles.Count ||
							vc2.NonStraight.Count > vc.NonStraight.Count ||
							vc2.Long.Count > vc.Long.Count)
						{
							// Such push would break some room connections
							// Or introduce new long connections
						}
						else if (rooms.Width * rooms.Height > Area.Width * Area.Height)
						{
							// Such push would make the grid bigger
						}
						else if (MMBArea.AreEqual(rooms, Area))
						{
							// Such push wouldn't change anything
						}
						else
						{
							// Mark the movement
							foreach (var m in measure.MovedRooms)
							{
								m.Room.ForceMark = m.Delta;
							}

							if (!AddRunStep())
							{
								return false;
							}

							// Do the move
							Area.ClearMarks();
							foreach (var m in measure.MovedRooms)
							{
								var newPos = new Point(m.Room.Position.Value.X + m.Delta.X,
									m.Room.Position.Value.Y + m.Delta.Y);

								m.Room.Position = newPos;
							}

							if (!AddRunStep())
							{
								return false;
							}

							continueToPush = true;
						}
					}
				}
			}

			Area.FixPlacementOfSingleExitRooms();

			return true;
		}

		private bool ObstacleFixRun(out bool fixes)
		{
			fixes = false;

			if (!Options.FixObstacles)
			{
				return true;
			}

			var vc = Area.BrokenConnections;
			while (vc.WithObstacles.Count > 0)
			{
				// Run while at least one connection could be fixed
				var connectionFixed = false;
				for (var i = 0; i < vc.WithObstacles.Count; ++i)
				{
					var wo = vc.WithObstacles[0];

					var roomsToDelete = new List<MMBRoom>();
					foreach (var o in wo.Obstacles)
					{
						roomsToDelete.Add(Area.GetRoomById(o));
					}

					if (roomsToDelete.Count == 0)
					{
						continue;
					}

					if (!RemoveRooms(roomsToDelete.ToArray()))
					{
						return false;
					}

					fixes = true;
					connectionFixed = true;
					break;
				}

				if (!connectionFixed)
				{
					break;
				}

				vc = Area.BrokenConnections;
			}

			return true;
		}

		private bool NonStraightFixRun(MMBRoom newRoom, out bool fixes)
		{
			fixes = false;
			if (!Options.FixNonStraight)
			{
				return true;
			}

			var vc = Area.BrokenConnections;
			while (vc.NonStraight.Count > 0)
			{
				// Run while at least one connection could be fixed
				var connectionFixed = false;
				for (var i = 0; i < vc.NonStraight.Count; ++i)
				{
					var ns = vc.NonStraight[i];

					// Try to straighten it
					var room1 = Area.GetRoomById(ns.SourceRoomId);
					var room2 = Area.GetRoomById(ns.TargetRoomId);
					var srr = StraightenConnection(room1, room2, ns.Direction);

					switch (srr)
					{
						case StraightenRoomResult.Success:
							fixes = true;
							connectionFixed = true;
							goto connectionFixed;

						case StraightenRoomResult.Fail:
							break;

						case StraightenRoomResult.OutOfSteps:
							return false;
					}
				}
			connectionFixed:;
				vc = Area.BrokenConnections;

				if (!connectionFixed)
				{
					break;
				}
			}

			return true;
		}

		private bool IntersectionsFixRun(MMBRoom newRoom, out bool fixes)
		{
			fixes = false;
			if (!Options.FixIntersected)
			{
				return true;
			}

			var vc = Area.BrokenConnections;
			while (vc.Intersections.Count > 0)
			{
				// Run while at least one connection could be fixed
				var connectionFixed = false;
				for (var i = 0; i < vc.Intersections.Count; ++i)
				{
					var intersection = vc.Intersections[i];
					if (newRoom.Id == intersection.SourceRoomId || newRoom.Id == intersection.TargetRoomId)
					{
						continue;
					}

					// Try to delete first room
					var rooms = Area.Clone();
					var deleteList = BuildRemoveList(new int[] { intersection.SourceRoomId });
					foreach (var id in deleteList)
					{
						rooms.GetRoomById(id).Position = null;
					}

					if (!ExistsInHistory(rooms))
					{
						var vc2 = rooms.BrokenConnections;
						if (vc2.Intersections.Count < vc.Intersections.Count)
						{
							if (!RemoveRooms((from id in deleteList select Area.GetRoomById(id)).ToArray()))
							{
								return false;
							}

							fixes = true;
							connectionFixed = true;
							break;
						}
					}

					// Try to delete the second room
					rooms = Area.Clone();
					deleteList = BuildRemoveList(new int[] { intersection.TargetRoomId });
					foreach (var id in deleteList)
					{
						rooms.GetRoomById(id).Position = null;
					}

					if (!ExistsInHistory(rooms))
					{
						var vc2 = rooms.BrokenConnections;
						if (vc2.Intersections.Count < vc.Intersections.Count)
						{
							if (!RemoveRooms((from id in deleteList select Area.GetRoomById(id)).ToArray()))
							{
								return false;
							}

							fixes = true;
							connectionFixed = true;
							break;
						}
					}
				}

				vc = Area.BrokenConnections;
				if (!connectionFixed)
				{
					break;
				}
			}

			return true;
		}

		private bool FixRun(MMBRoom newRoom)
		{
			// Connections fix run
			while (true)
			{
				bool obstaclesFixes;
				if (!ObstacleFixRun(out obstaclesFixes))
				{
					return false;
				}

				bool nonStraightFixes;
				if (!NonStraightFixRun(newRoom, out nonStraightFixes))
				{
					return false;
				}

				bool intersectionFixes;
				if (!IntersectionsFixRun(newRoom, out intersectionFixes))
				{
					return false;
				}

				if (!obstaclesFixes && !nonStraightFixes && !intersectionFixes)
				{
					// Run while at least one fix is possible
					break;
				}
			}


			return true;
		}

		private MapBuilderResult Process()
		{
			// Erase positions
			foreach (var room in Area.Rooms)
			{
				room.Position = null;
			}

			Area.ClearMarks();

			if (!Options.KeepSolitaryRooms)
			{
				// Remove solitary rooms
				var toDelete = (from r in Area.Rooms where r.Connections.Count == 0 select r).ToList();

				foreach (var room in toDelete)
				{
					Log($"Removed solitary room {room}");
					Area.DeleteRoom(room);
				}
			}

			if (!Options.KeepRoomsWithSingleOutsideExit)
			{
				// Remove single outside exit rooms
				var toDelete = new List<MMBRoom>();
				foreach (var room in Area.Rooms)
				{
					var connectionsFromRoomCount = (from c in room.Connections where c.Value.ConnectionType == MMBConnectionType.Forward select c).Count();
					var connectionsToRoomCount = (from r in Area.Rooms
												  where r.Id != room.Id &&
												  r.FindConnection(room.Id) != null &&
												  r.FindConnection(room.Id).ConnectionType != MMBConnectionType.Backward
												  select r).Count();

					if (connectionsFromRoomCount <= 1 && connectionsToRoomCount == 0)
					{
						// Delete
						toDelete.Add(room);
					}
				}

				foreach (var room in toDelete)
				{
					Log($"Removed single exit room {room}");
					Area.DeleteRoom(room);
				}
			}

			if (Area.Count == 0)
			{
				return null;
			}

			MMBRoom firstRoom = null;
			foreach (var room in Area.Rooms)
			{
				firstRoom = room;
			}

			firstRoom.Position = new Point(0, 0);
			_roomsQueue.Add(firstRoom.Id);

			BrokenConnectionsInfo vc;
			while (_roomsQueue.Count > 0 && Options.MaxSteps > _history.Count)
			{
				while (_roomsQueue.Count > 0 && Options.MaxSteps > _history.Count)
				{
					var id = _roomsQueue.Pop();
					var room = Area.GetRoomById(id);

					Log($"Processed {Area.PositionedRoomsCount}/{Area.Count} rooms. Step {_history.Count}. Grid Size: {Area.Width}x{Area.Height}");

					foreach (var pair in room.Connections)
					{
						var exitDir = pair.Key;
						if (pair.Value.RoomId == room.Id)
						{
							continue;
						}

						var newRoom = Area.GetRoomById(pair.Value.RoomId);
						if (newRoom.Position != null || _roomsQueue.WasAdded(pair.Value.RoomId))
						{
							continue;
						}

						var pos = room.Position.Value;
						var delta = exitDir.GetDelta();
						var newPos = new Point(pos.X + delta.X, pos.Y + delta.Y);

						vc = Area.BrokenConnections;

						// Expand grid either if the new position is occupied by a room
						// Or if it breaks existing connection
						var expandGrid = false;
						var existingRoom = Area.GetRoomByPosition(newPos);
						if (existingRoom != null)
						{
							MMBRoomConnection conn;
							if (MMBArea.IsSingleExitRoom(existingRoom, out conn) && (conn.Direction == MMBDirection.Up || conn.Direction == MMBDirection.Down))
							{
								// Delete such rooms instead of expanding the grid
								if (!RemoveRooms(new[] { existingRoom }))
								{
									goto finish;
								}
							}
							else
							{
								expandGrid = true;
							}
						}
						else
						{
							//
							var cloneRooms = Area.Clone();
							var cloneRoom = cloneRooms.GetRoomById(newRoom.Id);
							cloneRoom.Position = newPos;

							if (cloneRooms.BrokenConnections.WithObstacles.Count > vc.WithObstacles.Count)
							{
								expandGrid = true;
							}
						}

						if (expandGrid)
						{
							// Push rooms in the movement direction
							Area.ExpandGrid(newPos, delta);
							if (!AddRunStep())
							{
								goto finish;
							}
						}

						newRoom.Position = newPos;
						_roomsQueue.Add(newRoom.Id);

						if (!AddRunStep())
						{
							goto finish;
						}

						Area.FixPlacementOfSingleExitRooms();

						// Connections fix run
						if (!FixRun(newRoom))
						{
							break;
						}

						if (room.Position == null)
						{
							break;
						}
					}
				}

				if (!AddRunStep())
				{
					break;
				}

				// Update removed rooms
				foreach (var room in Area.Rooms)
				{
					if (room.Position == null)
					{
						continue;
					}

					if (_removedRooms.Contains(room.Id))
					{
						_removedRooms.Remove(room.Id);
					}
				}

				foreach (var roomId in _removedRooms)
				{
					// Find connected room that is processed
					var sourceRoom = Area.GetRoomById(roomId);
					foreach (var pair in sourceRoom.Connections)
					{
						foreach (var connectedRoom in Area.Rooms)
						{
							if (connectedRoom.Id == roomId || connectedRoom.Position == null)
							{
								continue;
							}

							var conn = connectedRoom.FindConnection(roomId);
							if (conn == null || _roomsQueue.WasAdded(connectedRoom.Id))
							{
								continue;
							}

							_roomsQueue.Add(connectedRoom.Id);
							break;
						}
					}
				}

				if (_roomsQueue.Count > 0 || _removedRooms.Count > 0)
				{
					continue;
				}

				// Finally deal with rooms that weren't reached
				foreach (var room in Area.Rooms)
				{
					if (room.Position != null)
					{
						continue;
					}

					// Unprocessed room
					if (!Options.KeepSolitaryRooms)
					{
						// Ignore if it has no connections
						if (room.Connections.Count == 0)
						{
							continue;
						}
					}

					// Put it to the bottom left
					room.Position = new Point(Area.RoomsRectangle.Left, Area.RoomsRectangle.Bottom + 1);
					_roomsQueue.Add(room.Id);

					break;
				}
			}

		finish:;
			var startCompactStep = _history.Count;

			if (Options.CompactMap)
			{
				var continueCompactRun = true;
				while (continueCompactRun)
				{
					var roomsClone = Area.Clone();
					if (!CompactRun(MMBDirection.East))
					{
						goto finish2;
					}

					if (!CompactRun(MMBDirection.South))
					{
						goto finish2;
					}
					if (!CompactRun(MMBDirection.West))
					{
						goto finish2;
					}

					if (!CompactRun(MMBDirection.North))
					{
						goto finish2;
					}

					continueCompactRun = !MMBArea.AreEqual(roomsClone, Area);
					break;
				}
			}

		finish2:;
			Log(_resultType.ToString());
			return new MapBuilderResult(_resultType, _history.ToArray(), startCompactStep);
		}

		public static MapBuilderResult Build(MMBProject project, Action<string> log)
		{
			var clone = project.Clone();

			var area = clone.Area;
			area.RemoveNonExistantConnections();

			// Set connections types
			foreach (var room in area.Rooms)
			{
				foreach (var connection in room.Connections.Values)
				{
					if (connection.RoomId == room.Id || connection.ConnectionType == MMBConnectionType.Backward)
					{
						continue;
					}

					var dir = connection.Direction;
					var oppDir = dir.GetOppositeDirection();

					var targetRoom = area.GetRoomById(connection.RoomId);
					if (targetRoom == null)
					{
						continue;
					}

					var foundOpposite = false;
					var oppositeConnection = targetRoom.FindConnection(room.Id);
					if (oppositeConnection != null &&
						oppDir == oppositeConnection.Direction)
					{
						foundOpposite = true;
					}

					if (foundOpposite)
					{
						connection.ConnectionType = MMBConnectionType.TwoWay;
					}
					else if (!targetRoom.Connections.ContainsKey(oppDir))
					{
						// Establish opposite backwards connection
						targetRoom.Connections[oppDir] = new MMBRoomConnection
						{
							Direction = oppDir,
							RoomId = room.Id,
							ConnectionType = MMBConnectionType.Backward
						};
					}
				}
			}

			var mapBuilder = new MapBuilder(clone, log);
			return mapBuilder.Process();
		}
	}
}