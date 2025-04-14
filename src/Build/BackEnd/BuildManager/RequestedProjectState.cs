// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Build.BackEnd;

#nullable disable

namespace Microsoft.Build.Execution
{
    /// <summary>
    /// Interface defining properties, items, and metadata of interest for a <see cref="BuildRequestData"/>.
    /// </summary>
    public class RequestedProjectState : ITranslatable, IEquatable<RequestedProjectState>
    {
        private List<string> _propertyFilters;
        private IDictionary<string, List<string>> _itemFilters;

        /// <summary>
        /// Properties of interest.
        /// </summary>
        public List<string> PropertyFilters
        {
            get => _propertyFilters;
            set => _propertyFilters = value;
        }

        /// <summary>
        /// Items and metadata of interest.
        /// </summary>
        public IDictionary<string, List<string>> ItemFilters
        {
            get => _itemFilters;
            set => _itemFilters = value;
        }

        /// <summary>
        /// Determines whether two RequestedProjectState instances are equal.
        /// </summary>
        public static bool operator ==(RequestedProjectState left, RequestedProjectState right) => left is null ? right is null : left.Equals(right);

        /// <summary>
        /// Determines whether two RequestedProjectState instances are not equal.
        /// </summary>
        public static bool operator !=(RequestedProjectState left, RequestedProjectState right) => !(left == right);

        /// <summary>
        /// Determines whether the specified object is equal to the current object.
        /// </summary>
        /// <param name="obj">The object to compare with the current object.</param>
        /// <returns>true if the specified object is equal to the current object; otherwise, false.</returns>
        public override bool Equals(object obj) => Equals(obj as RequestedProjectState);

        /// <summary>
        /// Determines whether the specified RequestedProjectState is equal to the current RequestedProjectState.
        /// </summary>
        /// <param name="other">The RequestedProjectState to compare with the current RequestedProjectState.</param>
        /// <returns>true if the specified RequestedProjectState is equal to the current RequestedProjectState; otherwise, false.</returns>
        public bool Equals(RequestedProjectState other)
        {
            if (other is null)
            {
                return false;
            }

            if (ReferenceEquals(this, other))
            {
                return true;
            }

            bool propertyFiltersEqual = ComparePropertyFilters(other);
            return !propertyFiltersEqual ? false : CompareItemFilters(other);
        }

        private bool ComparePropertyFilters(RequestedProjectState other)
        {
            if (PropertyFilters is null)
            {
                return other.PropertyFilters is null;
            }

            if (other.PropertyFilters is null || PropertyFilters.Count != other.PropertyFilters.Count)
            {
                return false;
            }

            HashSet<string> thisProperties = new(PropertyFilters, StringComparer.OrdinalIgnoreCase);

            return other.PropertyFilters.All(thisProperties.Contains);
        }

        private bool CompareItemFilters(RequestedProjectState other)
        {
            if (ItemFilters is null)
            {
                return other.ItemFilters is null;
            }

            if (other.ItemFilters is null || ItemFilters.Count != other.ItemFilters.Count)
            {
                return false;
            }

            foreach (KeyValuePair<string, List<string>> kvp in ItemFilters)
            {
                if (!other.ItemFilters.TryGetValue(kvp.Key, out List<string> otherMetadata))
                {
                    return false;
                }

                if (kvp.Value is null)
                {
                    if (otherMetadata is not null)
                    {
                        return false;
                    }

                    continue;
                }

                if (otherMetadata is null || kvp.Value.Count != otherMetadata.Count)
                {
                    return false;
                }

                HashSet<string> thisMetadata = new HashSet<string>(kvp.Value, StringComparer.OrdinalIgnoreCase);
                if (!otherMetadata.All(thisMetadata.Contains))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Returns the hash code for this instance.
        /// </summary>
        /// <returns>A 32-bit signed integer hash code.</returns>
        public override int GetHashCode()
        {
            unchecked
            {
                int hashCode = 17;

                if (PropertyFilters != null)
                {
                    // Sort keys for consistent hash code
                    foreach (string property in PropertyFilters.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                    {
                        hashCode = (hashCode * 31) + (property?.GetHashCode() ?? 0);
                    }
                }

                if (ItemFilters != null)
                {
                    // Sort keys for consistent hash code
                    foreach (string key in ItemFilters.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
                    {
                        hashCode = hashCode * 31 + key.GetHashCode();

                        List<string> metadataList = ItemFilters[key];
                        if (metadataList != null)
                        {
                            foreach (string metadata in metadataList.OrderBy(m => m, StringComparer.OrdinalIgnoreCase))
                            {
                                hashCode = (hashCode * 31) + (metadata?.GetHashCode() ?? 0);
                            }
                        }
                    }
                }

                return hashCode;
            }
        }

        /// <summary>
        /// Creates a deep copy of this instance.
        /// </summary>
        internal RequestedProjectState DeepClone()
        {
            RequestedProjectState result = new RequestedProjectState();
            if (PropertyFilters is not null)
            {
                result.PropertyFilters = [.. PropertyFilters];
            }
            if (ItemFilters is not null)
            {
                result.ItemFilters = ItemFilters.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value == null ? null : new List<string>(kvp.Value));
            }

            return result;
        }

        /// <summary>
        /// Returns true if this instance contains all property and item filters present in another instance.
        /// </summary>
        /// <param name="another">The instance to compare against.</param>
        /// <returns>True if this instance is equivalent or a strict subset of <paramref name="another"/>.</returns>
        internal bool IsSubsetOf(RequestedProjectState another)
        {
            if (PropertyFilters is null)
            {
                if (another.PropertyFilters is not null)
                {
                    // The instance to compare against has filtered props and we need everything -> not a subset.
                    return false;
                }
            }
            else if (another.PropertyFilters is not null)
            {
                HashSet<string> anotherPropertyFilters = new HashSet<string>(another.PropertyFilters);
                foreach (string propertyFilter in PropertyFilters)
                {
                    if (!anotherPropertyFilters.Contains(propertyFilter))
                    {
                        return false;
                    }
                }
            }

            if (ItemFilters is null)
            {
                if (another.ItemFilters is not null)
                {
                    // The instance to compare against has filtered items and we need everything -> not a subset.
                    return false;
                }
            }
            else if (another.ItemFilters is not null)
            {
                foreach (KeyValuePair<string, List<string>> kvp in ItemFilters)
                {
                    if (!another.ItemFilters.TryGetValue(kvp.Key, out List<string> metadata))
                    {
                        // The instance to compare against doesn't have this item -> not a subset.
                        return false;
                    }
                    if (kvp.Value is null)
                    {
                        if (metadata is not null)
                        {
                            // The instance to compare against has filtered metadata for this item and we need everything - not a subset.
                            return false;
                        }
                    }
                    else if (metadata is not null)
                    {
                        HashSet<string> anotherMetadata = new HashSet<string>(metadata);
                        foreach (string metadatum in kvp.Value)
                        {
                            if (!anotherMetadata.Contains(metadatum))
                            {
                                return false;
                            }
                        }
                    }
                }
            }

            return true;
        }

        void ITranslatable.Translate(ITranslator translator)
        {
            translator.Translate(ref _propertyFilters);
            translator.TranslateDictionary(ref _itemFilters, TranslateString, TranslateMetadataForItem, CreateItemMetadataDictionary);
        }

        private static IDictionary<string, List<string>> CreateItemMetadataDictionary(int capacity) => new Dictionary<string, List<string>>(capacity, StringComparer.OrdinalIgnoreCase);

        private static void TranslateMetadataForItem(ITranslator translator, ref List<string> list) => translator.Translate(ref list);

        private static void TranslateString(ITranslator translator, ref string s) => translator.Translate(ref s);
    }
}
