using System;
using System.Collections;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using System.Runtime.CompilerServices;
using Raven.Client;
using Raven.Server.Utils;
using Sparrow.Json;

namespace Raven.Server.Documents.Indexes.Static
{
    public class DynamicBlittableJson : DynamicObject, IEnumerable<object>, IBlittableJsonContainer
    {

        private const int DocumentIdFieldNameIndex = 0;
        private const int MetadataIdPropertyIndex = 1;
        private const int MetadataHasValueIndex = 2;
        private const int MetadataKeyIndex = 3;
        private const int MetadataIdIndex = 4;
        private const int MetadataEtagIndex = 5;
        private const int MetadataLastModifiedIndex = 6;

        static DynamicBlittableJson()
        {
            precomputedTable = new[]
            {
                new CompareKey( Constants.Documents.Indexing.Fields.DocumentIdFieldName, 0 ),
                new CompareKey( Constants.Documents.Metadata.IdProperty, 0 ),
                new CompareKey( Constants.Documents.Metadata.HasValue, 0 ), 
                new CompareKey( Constants.Documents.Metadata.Key, 0 ),
                new CompareKey( Constants.Documents.Metadata.Id, 1),
                new CompareKey( Constants.Documents.Metadata.Etag, 1),
                new CompareKey( Constants.Documents.Metadata.LastModified, 1)
            };
        }

        private Document _doc;
        public BlittableJsonReaderObject BlittableJson { get; private set; }

        public void EnsureMetadata()
        {
            _doc?.EnsureMetadata();
        }

        public DynamicBlittableJson(Document document)
        {
            Set(document);
        }

        public DynamicBlittableJson(BlittableJsonReaderObject blittableJson)
        {
            BlittableJson = blittableJson;
        }

        public void Set(Document document)
        {
            _doc = document;
            BlittableJson = document.Data;
        }

        public bool ContainsKey(string key)
        {
            return BlittableJson.GetPropertyNames().Contains(key);
        }

        public override bool TryGetMember(GetMemberBinder binder, out object result)
        {
            var name = binder.Name;
            return TryGetByName(name, out result);
        }

        public override bool TryGetIndex(GetIndexBinder binder, object[] indexes, out object result)
        {
            return TryGetByName((string)indexes[0], out result);
        }


        private struct CompareKey
        {
            public readonly string Key;
            public readonly int PrefixGroupIndex;
            public readonly char PrefixValue;
            public readonly int Length;

            public CompareKey(string key, int prefixGroup)
            {
                this.Key = key;
                this.PrefixGroupIndex = prefixGroup;
                this.PrefixValue = key[prefixGroup];
                this.Length = key.Length;
            }
        }

        private static readonly CompareKey[] precomputedTable;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool FastCompare(string name, int fieldLookup)
        {
            if (name.Length != precomputedTable[fieldLookup].Length)
                return false;

            int prefixGroup = precomputedTable[fieldLookup].PrefixGroupIndex;
            if (name[prefixGroup] != precomputedTable[fieldLookup].PrefixValue)
                return false;

            return string.Compare(name, precomputedTable[fieldLookup].Key, StringComparison.Ordinal) == 0;
        }

        private bool TryGetByName(string name, out object result)
        {
            // Using ordinal ignore case versions to avoid the cast of calling String.Equals with non interned values.
            if (FastCompare(name, DocumentIdFieldNameIndex) ||
                FastCompare(name, MetadataIdPropertyIndex))
            {
                if (BlittableJson.TryGetMember(name, out result))
                    return true;

                if (_doc == null)
                {
                    result = DynamicNullObject.Null;
                    return true;
                }

                result = _doc.Key;
                return true;
            }

            bool getResult = BlittableJson.TryGetMember(name, out result);

            if (getResult == false && _doc != null)
            {
                getResult = true;
                if (FastCompare(name, MetadataIdIndex))
                    result = _doc.Key;
                else if (FastCompare(name, MetadataEtagIndex))
                    result = _doc.Etag;
                else if (FastCompare(name, MetadataLastModifiedIndex))
                    result = _doc.LastModified;
                else
                    getResult = false;            
            }

            if (result == null && FastCompare(name, MetadataHasValueIndex))
            {
                result = getResult;
                return true;
            }

            if (getResult && result == null)
            {
                result = DynamicNullObject.ExplicitNull;
                return true;
            }

            if (getResult == false)
            {
                result = DynamicNullObject.Null;
                return true;
            }

            result = TypeConverter.ToDynamicType(result);

            if (FastCompare(name, MetadataKeyIndex))
            {
                ((DynamicBlittableJson) result)._doc = _doc;
            }

            return true;
        }

        public object this[string key]
        {
            get
            {
                object result;
                if (TryGetByName(key, out result) == false)
                    throw new InvalidOperationException($"Could not get '{key}' value of dynamic object");

                return result;
            }
        }

        public T Value<T>(string key)
        {
            return TypeConverter.Convert<T>(this[key], false);
        }

        public IEnumerator<object> GetEnumerator()
        {
            foreach (var propertyName in BlittableJson.GetPropertyNames())
            {
                yield return new KeyValuePair<object, object>(propertyName, TypeConverter.ToDynamicType(BlittableJson[propertyName]));
            }
        }

        public IEnumerable<object> Select(Func<object, object> func)
        {
            return new DynamicArray(Enumerable.Select(this, func));
        }

        public IEnumerable<object> OrderBy(Func<object, object> func)
        {
            return new DynamicArray(Enumerable.OrderBy(this, func));
        }

        public override string ToString()
        {
            return BlittableJson.ToString();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != GetType()) return false;

            return Equals((DynamicBlittableJson)obj);
        }

        protected bool Equals(DynamicBlittableJson other)
        {
            return Equals(BlittableJson, other.BlittableJson);
        }

        public override int GetHashCode()
        {
            return BlittableJson?.GetHashCode() ?? 0;
        }
    }
}