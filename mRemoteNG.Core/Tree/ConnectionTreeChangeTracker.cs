using System.Collections.Specialized;
using System.ComponentModel;
using mRemoteNG.Core.Container;

namespace mRemoteNG.Core.Tree
{
    /// <summary>
    /// Tracks whether a connection tree has unsaved changes. Containers bubble property and
    /// collection changes of all descendants, so watching the root node is enough.
    /// Expanding or collapsing a folder is view state and does not mark the tree dirty.
    /// </summary>
    public sealed class ConnectionTreeChangeTracker : IDisposable
    {
        private static readonly HashSet<string> IgnoredProperties = [nameof(ContainerInfo.IsExpanded)];

        private readonly ContainerInfo _root;
        private bool _isDirty;
        private bool _disposed;

        public ConnectionTreeChangeTracker(ContainerInfo root)
        {
            _root = root ?? throw new ArgumentNullException(nameof(root));
            _root.PropertyChanged += OnPropertyChanged;
            _root.CollectionChanged += OnCollectionChanged;
        }

        public bool IsDirty
        {
            get => _isDirty;
            private set
            {
                if (_isDirty == value) return;
                _isDirty = value;
                DirtyChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>Raised when <see cref="IsDirty"/> changes.</summary>
        public event EventHandler? DirtyChanged;

        /// <summary>Marks the tree as changed, for edits that raise no notification (e.g. inheritance flags).</summary>
        public void MarkDirty() => IsDirty = true;

        /// <summary>Marks the tree as saved.</summary>
        public void MarkClean() => IsDirty = false;

        private void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is not null && IgnoredProperties.Contains(e.PropertyName))
                return;
            IsDirty = true;
        }

        private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => IsDirty = true;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _root.PropertyChanged -= OnPropertyChanged;
            _root.CollectionChanged -= OnCollectionChanged;
        }
    }
}
