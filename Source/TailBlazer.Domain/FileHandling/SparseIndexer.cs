using System;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text;
using DynamicData;
using DynamicData.Binding;
using TailBlazer.Domain.Annotations;

namespace TailBlazer.Domain.FileHandling
{
    public class SparseIndexer: IDisposable
    {
        private readonly IDisposable _cleanUp;
        private int _endOfFile;
        private readonly ISourceList<SparseIndex> _indicies = new SourceList<SparseIndex>();

        public Encoding Encoding { get; }
        public FileInfo Info { get; }
        public int Compression { get;  }
        public int TailSize { get;  }
        public IObservable<SparseIndicies> Result { get; }
        
        public SparseIndexer([NotNull] FileInfo info,
            IObservable<Unit> refresher,
            int compression = 10,
            int tailSize = 1000000,
            int pageSize = 1000000,
            Encoding encoding = null,
            IScheduler scheduler = null)
        {
            if (info == null) throw new ArgumentNullException(nameof(info));
            scheduler = scheduler ?? Scheduler.Default;

            Info = info;
            Compression = compression;
            TailSize = tailSize;
            Encoding = encoding ?? info.GetEncoding();

            //0. create  a resulting index object from the collection of index fragments
            Result = _indicies
                .Connect()
                .Sort(SortExpressionComparer<SparseIndex>.Ascending(si => si.Start))
                .ToCollection()
                .Scan((SparseIndicies)null, (previous, notification) => new SparseIndicies(notification, previous, Encoding));

            //1. Get  full length of file
            var startScanningAt = (int)Math.Max(0, info.Length - tailSize);
            _endOfFile = (int)info.FindNextEndOfLinePosition(startScanningAt);
            

            //2. Scan the tail [TODO: put _endOfFile into observable]
            var tailScanner = refresher
                .StartWith(Unit.Default)
                .Select(_ => ScanTail(_endOfFile))
                .Where(tail => tail != null)
                .Select(tail =>
                {
                    //cannot use scan because reading head may update the last head
                    var previous = _indicies.Items.FirstOrDefault(si => si.Type == SpareIndexType.Tail);
                    return previous == null ? tail : new SparseIndex(tail, previous);
                })
                .Do(index=> _endOfFile= index.End)
                .Publish();

            var locker = new object();

            //3. Scan tail when we have the first result from the head
            var tailSubscriber = tailScanner
                .Synchronize(locker)
                .Subscribe(tail =>
                {
                    _indicies.Edit(innerList =>
                    {
                        var existing = innerList.FirstOrDefault(si => si.Type == SpareIndexType.Tail);
                        if (existing != null) _indicies.Remove(existing);
                        _indicies.Add(tail);
                    });
                });

            ////4. Scan the remainder of the file when the tail has been scanned
            var headSubscriber = tailScanner.FirstAsync()
                .Synchronize(locker)
                .Subscribe(tail =>
                {
                    var estimateLines = EstimateNumberOfLines(tail, info);
                    var estimate = new SparseIndex(0, tail.Start, compression, estimateLines, SpareIndexType.Page);
                    _indicies.Add(estimate);

                    scheduler.Schedule(() =>
                    {
                        var actual = Scan(0, tail.Start, compression);
                        _indicies.Edit(innerList =>
                        {
                            //check for overlapping index
                            if (actual.End > tail.Start)
                            {
                                //we have an over lapping index
                                //in this case we need to remove first entry from the head
                                var newTail = new SparseIndex(actual.End, tail.End, tail.Indicies.Skip(1).ToArray(), 1, tail.LineCount-1, SpareIndexType.Tail);
                                innerList.Remove(tail);
                                innerList.Add(newTail);
                            }
                            
                            innerList.Remove(estimate);
                            innerList.Add(actual);
                        });
                    });
                });

            _cleanUp = new CompositeDisposable(_indicies,
                tailSubscriber,
                headSubscriber,
                tailScanner.Connect());
        }

        private int EstimateNumberOfLines(SparseIndex tail, FileInfo info)
        {
            //Calculate estimate line count
            var averageLineLength = tail.Size/tail.LineCount;
            var estimatedLines = (info.Length - tail.Size) /averageLineLength;
            return (int) estimatedLines;
        }

        private SparseIndex ScanTail(int start)
        {
            return Scan(start,-1, 1);
        }

        private SparseIndex Scan(int start, int end, int compression)
        {
            int count = 0;
            int lastPosition = 0;
            using (var stream = File.Open(Info.FullName, FileMode.Open, FileAccess.Read, FileShare.Delete | FileShare.ReadWrite))
            {
                int[] lines;
                using (var reader = new StreamReaderExtended(stream, Encoding, false))
                { 
                    stream.Seek(start, SeekOrigin.Begin);
                    if (reader.EndOfStream) return null;

                    lines = reader.ScanLines(compression, i => i, (line, position) =>
                    {
                        lastPosition = position;
                        count++;
                        return end!=-1 && lastPosition >= end;

                    }).ToArray();
                }
                return new SparseIndex(start, lastPosition, lines, compression, count, end==-1 ? SpareIndexType.Tail : SpareIndexType.Page);
            }
        }

        public void Dispose()
        {
            _cleanUp.Dispose();
        }
    }
}