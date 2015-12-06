﻿// Copyright (c) Vladimir Sadov. All rights reserved.
//
// This file is distributed under the MIT License. See LICENSE.md for details.

using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace RethinkDb.Driver.Utils.NonBlocking.Counter
{
    [StructLayout(LayoutKind.Sequential)]
    public sealed class Counter64
    {
        public static readonly int MAX_CELL_COUNT = Environment.ProcessorCount * 2;
        public const int MAX_DRIFT = 1;

        private class Cell
        {
            [StructLayout(LayoutKind.Explicit)]
            public struct SpacedCounter
            {
                // 64 bytes - sizeof(long) - sizeof(objecHeader64)
                [FieldOffset(40)]
                public long cnt;
            }

            public SpacedCounter counter;
        }

        // spaced out counters
        private Cell[] cells;

        // how many cells we have
        private int cellCount;

        // default counter
        private long cnt;

        public Counter64()
        {
        }

        public long Value
        {
            get
            {
                var count = this.cnt;
                var cells = this.cells;

                if (cells != null)
                {
                    for (int i = 0; i < cells.Length; i++)
                    {
                        var cell = cells[i];
                        if (cell != null)
                        {
                            count += cell.counter.cnt;
                        }
                        else
                        {
                            break;
                        }
                    }
                }

                return count;
            }
        }

        internal long EstimatedValue
        {
            get
            {
                return this.Value;

                // TODO: is there a scenario where the following is cheaper?
                //       we woud need to have a lot of counters.

                //var curTicks = DateTime.UtcNow.Ticks;
                //// more than millisecond passed?
                //if (curTicks - lastTicks > TimeSpan.TicksPerMillisecond)
                //{
                //    lastCnt = get();
                //    lastTicks = curTicks;
                //}

                //return lastCnt;
            }
        }

        public void Increment()
        {
            Cell cell = null;

            int curCellCount = this.cellCount;
            if (curCellCount > 1 & this.cells != null)
            {
                cell = this.cells[GetIndex(curCellCount)];
            }

            var drift = cell == null ?
                increment(ref cnt) :
                increment(ref cell.counter.cnt);

            if (drift > MAX_DRIFT)
            {
                TryAddCell(curCellCount);
            }
        }

        public void Add(int c)
        {
            Cell cell = null;

            int curCellCount = this.cellCount;
            if (curCellCount > 1 & this.cells != null)
            {
                cell = this.cells[GetIndex(curCellCount)];
            }

            var drift = cell == null ?
                add(ref cnt, c) :
                add(ref cell.counter.cnt, c);

            if (drift > MAX_DRIFT)
            {
                TryAddCell(curCellCount);
            }
        }

        public void Decrement()
        {
            Cell cell = null;

            int curCellCount = this.cellCount;
            if (curCellCount > 1 & this.cells != null)
            {
                cell = this.cells[GetIndex(curCellCount)];
            }

            var drift = cell == null ?
                decrement(ref cnt) :
                decrement(ref cell.counter.cnt);

            if (drift > MAX_DRIFT)
            {
                TryAddCell(curCellCount);
            }
        }

        private static long increment(ref long val)
        {
            return -val + Interlocked.Increment(ref val) - 1;
        }

        private static long add(ref long val, int inc)
        {
            return -val + Interlocked.Add(ref val, inc) - inc;
        }

        private static long decrement(ref long val)
        {
            return val - Interlocked.Decrement(ref val) - 1;
        }

        private static int GetIndex(int cellCount)
        {
            return Environment.CurrentManagedThreadId % cellCount;
        }

        private void TryAddCell(int curCellCount)
        {
            if (curCellCount < MAX_CELL_COUNT)
            {
                var cells = this.cells;
                if (cells == null)
                {
                    var newCells = new Cell[MAX_CELL_COUNT];
                    cells = Interlocked.CompareExchange(ref this.cells, newCells, null) ?? newCells;
                }

                if (cells[curCellCount] == null)
                {
                    Interlocked.CompareExchange(ref cells[curCellCount], new Cell(), null);
                }

                if (this.cellCount == curCellCount)
                {
                    Interlocked.CompareExchange(ref this.cellCount, curCellCount + 1, curCellCount);
                    //if (Interlocked.CompareExchange(ref this.cellCount, curCellCount + 1, curCellCount) == curCellCount)
                    //{
                    //    System.Console.WriteLine(curCellCount + 1);
                    //}
                }
            }
        }
    }
}
