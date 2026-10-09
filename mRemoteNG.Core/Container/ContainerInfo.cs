using System.Collections.Specialized;
using System.ComponentModel;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Tree;

namespace mRemoteNG.Core.Container
{
    /// <summary>
    /// Represents a folder/container in the connection tree.
    /// Contains child connections and other containers.
    /// </summary>
    public class ContainerInfo : ConnectionInfo, INotifyCollectionChanged
    {
        private bool _isExpanded;

        public List<ConnectionInfo> Children { get; } = [];

        public bool IsExpanded
        {
            get => _isExpanded;
            set => SetField(ref _isExpanded, value);
        }

        public override bool IsContainer
        {
            get => true;
            set { }
        }

        public ContainerInfo(string uniqueId)
            : base(uniqueId)
        {
            Name = "New Folder";
            IsExpanded = true;
        }

        public ContainerInfo()
            : this(Guid.NewGuid().ToString())
        {
        }

        public override TreeNodeType GetTreeNodeType()
        {
            return TreeNodeType.Container;
        }

        public bool HasChildren()
        {
            return Children.Count > 0;
        }

        public void AddChild(ConnectionInfo newChildItem)
        {
            AddChildAt(newChildItem, Children.Count);
        }

        public void AddChildAbove(ConnectionInfo newChildItem, ConnectionInfo reference)
        {
            int newChildIndex = Children.IndexOf(reference);
            if (newChildIndex < 0)
                newChildIndex = Children.Count;
            AddChildAt(newChildItem, newChildIndex);
        }

        public void AddChildBelow(ConnectionInfo newChildItem, ConnectionInfo reference)
        {
            int newChildIndex = Children.IndexOf(reference) + 1;
            if (newChildIndex > Children.Count || newChildIndex < 1)
                newChildIndex = Children.Count;
            AddChildAt(newChildItem, newChildIndex);
        }

        public virtual void AddChildAt(ConnectionInfo newChildItem, int index)
        {
            if (Children.Contains(newChildItem)) return;
            newChildItem.Parent?.RemoveChild(newChildItem);
            newChildItem.Parent = this;
            Children.Insert(index, newChildItem);
            SubscribeToChildEvents(newChildItem);
            RaiseCollectionChangedEvent(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, newChildItem));
        }

        public void AddChildRange(IEnumerable<ConnectionInfo> newChildren)
        {
            foreach (var child in newChildren)
                AddChild(child);
        }

        public virtual void RemoveChild(ConnectionInfo removalTarget)
        {
            if (!Children.Contains(removalTarget)) return;
            removalTarget.Parent = null;
            Children.Remove(removalTarget);
            UnsubscribeToChildEvents(removalTarget);
            RaiseCollectionChangedEvent(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, removalTarget));
        }

        public void RemoveChildRange(IEnumerable<ConnectionInfo> removalTargets)
        {
            foreach (var child in removalTargets.ToList())
                RemoveChild(child);
        }

        public void SetChildPosition(ConnectionInfo child, int newIndex)
        {
            int originalIndex = Children.IndexOf(child);
            if (originalIndex < 0 || originalIndex == newIndex || newIndex < 0) return;
            Children.Remove(child);
            if (newIndex > Children.Count) newIndex = Children.Count;
            Children.Insert(newIndex, child);
            RaiseCollectionChangedEvent(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Move, child, newIndex, originalIndex));
        }

        public void Sort(ListSortDirection sortDirection = ListSortDirection.Ascending)
        {
            SortOn(c => c.Name, sortDirection);
        }

        public void SortOn<TProperty>(Func<ConnectionInfo, TProperty> propertyToCompare, ListSortDirection sortDirection = ListSortDirection.Ascending)
            where TProperty : IComparable<TProperty>
        {
            Children.Sort((a, b) =>
            {
                int result = propertyToCompare(a).CompareTo(propertyToCompare(b));
                return sortDirection == ListSortDirection.Descending ? -result : result;
            });
            RaiseCollectionChangedEvent(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }

        public void SortRecursive(ListSortDirection sortDirection = ListSortDirection.Ascending)
        {
            SortOnRecursive(c => c.Name, sortDirection);
        }

        public void SortOnRecursive<TProperty>(Func<ConnectionInfo, TProperty> propertyToCompare, ListSortDirection sortDirection = ListSortDirection.Ascending)
            where TProperty : IComparable<TProperty>
        {
            foreach (var child in Children.OfType<ContainerInfo>())
                child.SortOnRecursive(propertyToCompare, sortDirection);
            SortOn(propertyToCompare, sortDirection);
        }

        public override ConnectionInfo Clone()
        {
            ContainerInfo newContainer = new();
            newContainer.CopyFrom(this);
            newContainer.Inheritance = Inheritance.Clone(newContainer);
            foreach (var child in Children.ToArray())
            {
                var newChild = child.Clone();
                newChild.RemoveParent();
                newContainer.AddChild(newChild);
            }
            return newContainer;
        }

        public IEnumerable<ConnectionInfo> GetRecursiveChildList()
        {
            List<ConnectionInfo> childList = [];
            foreach (var child in Children)
            {
                childList.Add(child);
                if (child is ContainerInfo container)
                    childList.AddRange(container.GetRecursiveChildList());
            }
            return childList;
        }

        public IEnumerable<ConnectionInfo> GetRecursiveFavoriteChildList()
        {
            List<ConnectionInfo> childList = [];
            foreach (var child in Children)
            {
                if (child.Favorite && child.GetTreeNodeType() == TreeNodeType.Connection)
                    childList.Add(child);
                if (child is ContainerInfo container)
                    childList.AddRange(container.GetRecursiveFavoriteChildList());
            }
            return childList;
        }

        public void ApplyConnectionPropertiesToChildren()
        {
            foreach (var child in GetRecursiveChildList())
                child.CopyFrom(this);
        }

        public void ApplyInheritancePropertiesToChildren()
        {
            foreach (var child in GetRecursiveChildList())
                child.Inheritance = Inheritance.Clone(child);
        }

        protected virtual void SubscribeToChildEvents(ConnectionInfo child)
        {
            child.PropertyChanged += RaisePropertyChangedEvent;
            if (child is ContainerInfo container)
                container.CollectionChanged += RaiseCollectionChangedEvent;
        }

        protected virtual void UnsubscribeToChildEvents(ConnectionInfo child)
        {
            child.PropertyChanged -= RaisePropertyChangedEvent;
            if (child is ContainerInfo container)
                container.CollectionChanged -= RaiseCollectionChangedEvent;
        }

        public event NotifyCollectionChangedEventHandler? CollectionChanged;

        private void RaiseCollectionChangedEvent(object? sender, NotifyCollectionChangedEventArgs args)
        {
            CollectionChanged?.Invoke(sender, args);
        }
    }
}
