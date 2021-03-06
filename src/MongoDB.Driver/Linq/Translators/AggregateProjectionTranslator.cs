﻿/* Copyright 2010-2014 MongoDB Inc.
*
* Licensed under the Apache License, Version 2.0 (the "License");
* you may not use this file except in compliance with the License.
* You may obtain a copy of the License at
*
* http://www.apache.org/licenses/LICENSE-2.0
*
* Unless required by applicable law or agreed to in writing, software
* distributed under the License is distributed on an "AS IS" BASIS,
* WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
* See the License for the specific language governing permissions and
* limitations under the License.
* 
*/

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver.Linq.Expressions;
using MongoDB.Driver.Linq.Processors;
using MongoDB.Driver.Linq.Utils;

namespace MongoDB.Driver.Linq.Translators
{
    internal class AggregateProjectionTranslator
    {
        public static ProjectionInfo<TResult> TranslateProject<TDocument, TResult>(Expression<Func<TDocument, TResult>> projector, IBsonSerializer<TDocument> parameterSerializer, IBsonSerializerRegistry serializerRegistry)
        {
            if (projector.Body.NodeType != ExpressionType.New)
            {
                throw new NotSupportedException("Must use an anonymous type for constructing $project pipeline operators.");
            }

            var binder = new SerializationInfoBinder(BsonSerializer.SerializerRegistry);
            var boundExpression = BindSerializationInfo(binder, projector, parameterSerializer);
            var projectionSerializer = (IBsonSerializer<TResult>)SerializerBuilder.Build(boundExpression, serializerRegistry);
            var projection = ProjectionBuilder.Build(boundExpression).AsBsonDocument;

            if (!projection.Contains("_id"))
            {
                projection.Add("_id", 0); // we don't want the id back unless we asked for it...
            }

            return new ProjectionInfo<TResult>(projection, projectionSerializer);
        }

        public static ProjectionInfo<TResult> TranslateGroup<TKey, TDocument, TResult>(Expression<Func<TDocument, TKey>> idProjector, Expression<Func<IGrouping<TKey, TDocument>, TResult>> groupProjector, IBsonSerializer<TDocument> parameterSerializer, IBsonSerializerRegistry serializerRegistry)
        {
            if (groupProjector.Body.NodeType != ExpressionType.New)
            {
                throw new NotSupportedException("Must use an anonymous type for constructing $group pipeline operators.");
            }

            var keyBinder = new SerializationInfoBinder(serializerRegistry);
            var boundKeyExpression = BindSerializationInfo(keyBinder, idProjector, parameterSerializer);
            if (!(boundKeyExpression is IBsonSerializationInfoExpression))
            {
                var keySerializer = SerializerBuilder.Build(boundKeyExpression, serializerRegistry);
                boundKeyExpression = new DocumentExpression(
                    boundKeyExpression,
                    new BsonSerializationInfo(null, keySerializer, typeof(TKey)));
            }

            var groupBinder = new GroupSerializationInfoBinder(BsonSerializer.SerializerRegistry);
            groupBinder.RegisterMemberReplacement(typeof(IGrouping<TKey, TDocument>).GetProperty("Key"), boundKeyExpression);
            var groupSerializer = new ArraySerializer<TDocument>(parameterSerializer);
            var boundGroupExpression = BindSerializationInfo(groupBinder, groupProjector, groupSerializer);
            var projectionSerializer = (IBsonSerializer<TResult>)SerializerBuilder.Build(boundGroupExpression, serializerRegistry);
            var projection = ProjectionBuilder.Build(boundGroupExpression).AsBsonDocument;

            // must have an "_id" in a group document
            if (!projection.Contains("_id"))
            {
                var idProjection = ProjectionBuilder.Build(boundKeyExpression);
                projection.InsertAt(0, new BsonElement("_id", idProjection));
            }

            return new ProjectionInfo<TResult>(projection, projectionSerializer);
        }

        private static Expression BindSerializationInfo(SerializationInfoBinder binder, LambdaExpression node, IBsonSerializer parameterSerializer)
        {
            var evaluatedBody = PartialEvaluator.Evaluate(node.Body);
            var parameterSerializationInfo = new BsonSerializationInfo(null, parameterSerializer, parameterSerializer.ValueType);
            var parameterExpression = new DocumentExpression(node.Parameters[0], parameterSerializationInfo);
            binder.RegisterParameterReplacement(node.Parameters[0], parameterExpression);
            return binder.Bind(evaluatedBody);
        }

        private class ProjectionBuilder
        {
            public static BsonValue Build(Expression projector)
            {
                var builder = new ProjectionBuilder();
                return builder.ResolveValue(projector);
            }

            private BsonValue BuildValue(Expression node)
            {
                switch (node.NodeType)
                {
                    case ExpressionType.Add:
                    case ExpressionType.AddChecked:
                        return BuildAdd((BinaryExpression)node);
                    case ExpressionType.And:
                    case ExpressionType.AndAlso:
                        return BuildOperation((BinaryExpression)node, "$and", true);
                    case ExpressionType.ArrayLength:
                        return new BsonDocument("$size", ResolveValue(((UnaryExpression)node).Operand));
                    case ExpressionType.Call:
                        return BuildMethodCall((MethodCallExpression)node);
                    case ExpressionType.Coalesce:
                        return BuildOperation((BinaryExpression)node, "$ifNull", false);
                    case ExpressionType.Conditional:
                        return BuildConditional((ConditionalExpression)node);
                    case ExpressionType.Constant:
                        var value = BsonValue.Create(((ConstantExpression)node).Value);
                        var stringValue = value as BsonString;
                        if (stringValue != null && stringValue.Value.StartsWith("$"))
                        {
                            value = new BsonDocument("$literal", value);
                        }
                        // TODO: there may be other instances where we should use a literal...
                        // but I can't think of any yet.
                        return value;
                    case ExpressionType.Convert:
                    case ExpressionType.ConvertChecked:
                        return BuildValue(((UnaryExpression)node).Operand);
                    case ExpressionType.Divide:
                        return BuildOperation((BinaryExpression)node, "$divide", false);
                    case ExpressionType.Equal:
                        return BuildOperation((BinaryExpression)node, "$eq", false);
                    case ExpressionType.GreaterThan:
                        return BuildOperation((BinaryExpression)node, "$gt", false);
                    case ExpressionType.GreaterThanOrEqual:
                        return BuildOperation((BinaryExpression)node, "$gte", false);
                    case ExpressionType.LessThan:
                        return BuildOperation((BinaryExpression)node, "$lt", false);
                    case ExpressionType.LessThanOrEqual:
                        return BuildOperation((BinaryExpression)node, "$lte", false);
                    case ExpressionType.MemberAccess:
                        return BuildMemberAccess((MemberExpression)node);
                    case ExpressionType.Modulo:
                        return BuildOperation((BinaryExpression)node, "$mod", false);
                    case ExpressionType.Multiply:
                    case ExpressionType.MultiplyChecked:
                        return BuildOperation((BinaryExpression)node, "$multiply", true);
                    case ExpressionType.New:
                        return BuildNew((NewExpression)node);
                    case ExpressionType.Not:
                        return BuildNot((UnaryExpression)node);
                    case ExpressionType.NotEqual:
                        return BuildOperation((BinaryExpression)node, "$ne", false);
                    case ExpressionType.Or:
                    case ExpressionType.OrElse:
                        return BuildOperation((BinaryExpression)node, "$or", true);
                    case ExpressionType.Subtract:
                    case ExpressionType.SubtractChecked:
                        return BuildOperation((BinaryExpression)node, "$subtract", false);
                    case ExpressionType.Extension:
                        var mongoExpression = node as MongoExpression;
                        if (mongoExpression != null)
                        {
                            switch (mongoExpression.MongoNodeType)
                            {
                                case MongoExpressionType.Aggregation:
                                    return BuildAggregation((AggregationExpression)node);
                            }
                        }
                        break;
                }

                var message = string.Format("{0} is an unsupported node type in an $project or $group pipeline operator.", node.NodeType);
                throw new NotSupportedException(message);
            }

            private BsonValue BuildAdd(BinaryExpression node)
            {
                var op = "$add";
                if (node.Left.Type == typeof(string))
                {
                    op = "$concat";
                }

                return BuildOperation(node, op, true);
            }

            private BsonValue BuildAggregation(AggregationExpression node)
            {
                switch (node.AggregationType)
                {
                    case AggregationType.AddToSet:
                        return new BsonDocument("$addToSet", ResolveValue(node.Argument));
                    case AggregationType.Average:
                        return new BsonDocument("$avg", ResolveValue(node.Argument));
                    case AggregationType.First:
                        return new BsonDocument("$first", ResolveValue(node.Argument));
                    case AggregationType.Last:
                        return new BsonDocument("$last", ResolveValue(node.Argument));
                    case AggregationType.Max:
                        return new BsonDocument("$max", ResolveValue(node.Argument));
                    case AggregationType.Min:
                        return new BsonDocument("$min", ResolveValue(node.Argument));
                    case AggregationType.Push:
                        return new BsonDocument("$push", ResolveValue(node.Argument));
                    case AggregationType.Sum:
                        return new BsonDocument("$sum", ResolveValue(node.Argument));
                }

                // we should never ever get here.
                throw new MongoInternalException("Unrecognized aggregation type.");
            }

            private BsonValue BuildConditional(ConditionalExpression node)
            {
                var condition = ResolveValue(node.Test);
                var truePart = ResolveValue(node.IfTrue);
                var falsePart = ResolveValue(node.IfFalse);

                return new BsonDocument("$cond", new BsonArray(new[] { condition, truePart, falsePart }));
            }

            private BsonValue BuildMemberAccess(MemberExpression node)
            {
                BsonValue result;
                if (node.Expression.Type == typeof(DateTime)
                    && TryBuildDateTimeMemberAccess(node, out result))
                {
                    return result;
                }

                if (node.Expression != null
                    && (TypeHelper.ImplementsInterface(node.Expression.Type, typeof(ICollection<>))
                        || TypeHelper.ImplementsInterface(node.Expression.Type, typeof(ICollection)))
                    && node.Member.Name == "Count")
                {
                    return new BsonDocument("$size", ResolveValue(node.Expression));
                }

                var message = string.Format("Member {0} of type {1} are not supported in a $project or $group pipeline operator.", node.Member.Name, node.Member.DeclaringType);
                throw new NotSupportedException(message);
            }

            private BsonValue BuildMethodCall(MethodCallExpression node)
            {
                BsonValue result;
                if (MongoExpressionVisitor.IsLinqMethod(node) && TryBuildLinqMethodCall(node, out result))
                {
                    return result;
                }

                if (node.Object != null
                    && node.Object.Type == typeof(string)
                    && TryBuildStringMethodCall(node, out result))
                {
                    return result;
                }

                if (node.Object != null
                    && node.Object.Type.IsGenericType
                    && node.Object.Type.GetGenericTypeDefinition() == typeof(HashSet<>)
                    && TryBuildHashSetMethodCall(node, out result))
                {
                    return result;
                }

                if (node.Object != null
                    && node.Method.Name == "CompareTo"
                    && (TypeHelper.ImplementsInterface(node.Object.Type, typeof(IComparable<>))
                        || TypeHelper.ImplementsInterface(node.Object.Type, typeof(IComparable))))
                {
                    return new BsonDocument("$cmp", new BsonArray(new[] { ResolveValue(node.Object), ResolveValue(node.Arguments[0]) }));
                }

                if (node.Object != null
                    && node.Method.Name == "Equals"
                    && node.Arguments.Count == 1)
                {
                    return new BsonDocument("$eq", new BsonArray(new[] { ResolveValue(node.Object), ResolveValue(node.Arguments[0]) }));
                }

                var message = string.Format("{0} of type {1} is an unsupported method in a $project or $group pipeline operator.", node.Method.Name, node.Method.DeclaringType);
                throw new NotSupportedException(message);
            }

            private BsonValue BuildNew(NewExpression node)
            {
                BsonDocument doc = new BsonDocument();
                var parameters = node.Constructor.GetParameters();
                for (int i = 0; i < node.Arguments.Count; i++)
                {
                    var value = ResolveValue(node.Arguments[i]);
                    doc.Add(parameters[i].Name, value);
                }

                return doc;
            }

            private BsonValue BuildNot(UnaryExpression node)
            {
                var operand = ResolveValue(node.Operand);
                if (operand.IsBsonDocument)
                {
                    operand = new BsonArray().Add(operand);
                }
                return new BsonDocument("$not", operand);
            }

            private BsonValue BuildOperation(BinaryExpression node, string op, bool canBeFlattened)
            {
                var left = ResolveValue(node.Left);
                var right = ResolveValue(node.Right);

                // some operations take an array as the argument.
                // we want to flatten binary values into the top-level 
                // array if they are flattenable :).
                if (canBeFlattened && left.IsBsonDocument && left.AsBsonDocument.Contains(op) && left[op].IsBsonArray)
                {
                    left[op].AsBsonArray.Add(right);
                    return left;
                }

                return new BsonDocument(op, new BsonArray(new[] { left, right }));
            }

            private BsonValue ResolveValue(Expression node)
            {
                var fieldExpression = node as FieldExpression;
                if (fieldExpression != null)
                {
                    return "$" + fieldExpression.SerializationInfo.ElementName;
                }

                var documentExpression = node as DocumentExpression;
                if (documentExpression != null)
                {
                    return ResolveValue(documentExpression.Expression);
                }

                return BuildValue(node);
            }

            private bool TryBuildDateTimeMemberAccess(MemberExpression node, out BsonValue result)
            {
                result = null;
                var field = ResolveValue(node.Expression);
                switch (node.Member.Name)
                {
                    case "Day":
                        result = new BsonDocument("$dayOfMonth", field);
                        return true;
                    case "DayOfWeek":
                        // The server's day of week values are 1 greater than
                        // .NET's DayOfWeek enum values
                        result = new BsonDocument("$subtract", new BsonArray
                        {
                            new BsonDocument("$dayOfWeek", field),
                            new BsonInt32(1)
                        });
                        return true;
                    case "DayOfYear":
                        result = new BsonDocument("$dayOfYear", field);
                        return true;
                    case "Hour":
                        result = new BsonDocument("$hour", field);
                        return true;
                    case "Millisecond":
                        result = new BsonDocument("$millisecond", field);
                        return true;
                    case "Minute":
                        result = new BsonDocument("$minute", field);
                        return true;
                    case "Month":
                        result = new BsonDocument("$month", field);
                        return true;
                    case "Second":
                        result = new BsonDocument("$second", field);
                        return true;
                    case "Year":
                        result = new BsonDocument("$year", field);
                        return true;
                }

                return false;
            }

            private bool TryBuildHashSetMethodCall(MethodCallExpression node, out BsonValue result)
            {
                result = null;
                switch (node.Method.Name)
                {
                    case "IsSubsetOf":
                        result = new BsonDocument("$setIsSubset", new BsonArray(new[] 
                        { 
                            ResolveValue(node.Object), 
                            ResolveValue(node.Arguments[0])
                        }));
                        return true;
                    case "SetEquals":
                        result = new BsonDocument("$setEquals", new BsonArray(new[] 
                        { 
                            ResolveValue(node.Object), 
                            ResolveValue(node.Arguments[0])
                        }));
                        return true;
                }

                return false;
            }

            private bool TryBuildLinqMethodCall(MethodCallExpression node, out BsonValue result)
            {
                result = null;
                switch (node.Method.Name)
                {
                    case "All":
                        if (TryBuildMap(node, out result))
                        {
                            result = new BsonDocument("$allElementsTrue", result);
                            return true;
                        }
                        break;
                    case "Any":
                        if (node.Arguments.Count == 1)
                        {
                            result = new BsonDocument("$gt", new BsonArray(new BsonValue[] 
                            {
                                new BsonDocument("$size", ResolveValue(node.Arguments[0])),
                                0
                            }));
                            return true;
                        }
                        else if (TryBuildMap(node, out result))
                        {
                            result = new BsonDocument("$anyElementTrue", result);
                            return true;
                        }
                        break;
                    case "Count":
                    case "LongCount":
                        if (node.Arguments.Count == 1)
                        {
                            result = new BsonDocument("$size", ResolveValue(node.Arguments[0]));
                            return true;
                        }
                        break;
                    case "Except":
                        if (node.Arguments.Count == 2)
                        {
                            result = new BsonDocument("$setDifference", new BsonArray(new[] 
                            { 
                                ResolveValue(node.Arguments[0]), 
                                ResolveValue(node.Arguments[1]) 
                            }));
                            return true;
                        }
                        break;
                    case "Intersect":
                        if (node.Arguments.Count == 2)
                        {
                            result = new BsonDocument("$setIntersection", new BsonArray(new[] 
                            { 
                                ResolveValue(node.Arguments[0]), 
                                ResolveValue(node.Arguments[1]) 
                            }));
                            return true;
                        }
                        break;
                    case "Select":
                        if (TryBuildMap(node, out result))
                        {
                            return true;
                        }
                        break;
                    case "Union":
                        if (node.Arguments.Count == 2)
                        {
                            result = new BsonDocument("$setUnion", new BsonArray(new[] 
                            { 
                                ResolveValue(node.Arguments[0]), 
                                ResolveValue(node.Arguments[1])
                            }));
                            return true;
                        }
                        break;
                }

                return false;
            }

            private bool TryBuildMap(MethodCallExpression node, out BsonValue result)
            {
                result = null;
                var sourceSerializationExpression = node.Arguments[0] as IBsonSerializationInfoExpression;
                if (sourceSerializationExpression != null)
                {
                    var lambda = MongoExpressionVisitor.GetLambda(node.Arguments[1]);
                    if (lambda.Body is IBsonSerializationInfoExpression)
                    {
                        result = ResolveValue(lambda.Body);
                        return true;
                    }

                    var inputValue = ResolveValue(node.Arguments[0]);
                    var asValue = lambda.Parameters[0].Name;

                    // HACK: need to add a leading $ sign to the replacement because of how we resolve values.
                    var body = FieldNameReplacer.Replace(lambda.Body, sourceSerializationExpression.SerializationInfo.ElementName, "$" + asValue);
                    var inValue = ResolveValue(body);

                    result = new BsonDocument("$map", new BsonDocument
                            {
                                { "input", inputValue },
                                { "as", asValue },
                                { "in", inValue }
                            });
                    return true;
                }

                return false;
            }

            private bool TryBuildStringMethodCall(MethodCallExpression node, out BsonValue result)
            {
                result = null;
                var field = ResolveValue(node.Object);
                switch (node.Method.Name)
                {
                    case "Equals":
                        if (node.Arguments.Count == 2 && node.Arguments[1].NodeType == ExpressionType.Constant)
                        {
                            var comparisonType = (StringComparison)((ConstantExpression)node.Arguments[1]).Value;
                            switch (comparisonType)
                            {
                                case StringComparison.OrdinalIgnoreCase:
                                    result = new BsonDocument("$eq",
                                        new BsonArray(new BsonValue[] 
                                        {
                                            new BsonDocument("$strcasecmp", new BsonArray(new[] { field, ResolveValue(node.Arguments[0]) })),
                                            0
                                        }));
                                    return true;
                                case StringComparison.Ordinal:
                                    result = new BsonDocument("$eq", new BsonArray(new[] { field, ResolveValue(node.Arguments[0]) }));
                                    return true;
                                default:
                                    throw new NotSupportedException("Only Ordinal and OrdinalIgnoreCase are supported for string comparisons.");
                            }
                        }
                        break;
                    case "Substring":
                        if (node.Arguments.Count == 2)
                        {
                            result = new BsonDocument("$substr", new BsonArray(new[] 
                            { 
                                field, 
                                ResolveValue(node.Arguments[0]), 
                                ResolveValue(node.Arguments[1])
                            }));
                            return true;
                        }
                        break;
                    case "ToLower":
                    case "ToLowerInvariant":
                        if (node.Arguments.Count == 0)
                        {
                            result = new BsonDocument("$toLower", field);
                            return true;
                        }
                        break;
                    case "ToUpper":
                    case "ToUpperInvariant":
                        if (node.Arguments.Count == 0)
                        {
                            result = new BsonDocument("$toUpper", field);
                            return true;
                        }
                        break;
                }

                return false;
            }
        }

        private class SerializerBuilder
        {
            public static IBsonSerializer Build(Expression node, IBsonSerializerRegistry serializerRegistry)
            {
                var builder = new SerializerBuilder(serializerRegistry);
                return builder.Build(node);
            }

            private IBsonSerializerRegistry _serializerRegistry;

            private SerializerBuilder(IBsonSerializerRegistry serializerRegistry)
            {
                _serializerRegistry = serializerRegistry;
            }

            public IBsonSerializer Build(Expression node)
            {
                if (node is IBsonSerializationInfoExpression)
                {
                    return ((IBsonSerializationInfoExpression)node).SerializationInfo.Serializer;
                }

                if (node.NodeType == ExpressionType.New)
                {
                    return BuildNew((NewExpression)node);
                }

                return _serializerRegistry.GetSerializer(node.Type);
            }

            protected IBsonSerializer BuildNew(NewExpression node)
            {
                if (node.Members != null)
                {
                    return BuildSerializerForAnonymousType(node);
                }

                throw new NotSupportedException("Only new anomymous type expressions are allowed in $project or $group pipeline operators.");
            }

            private IBsonSerializer BuildSerializerForAnonymousType(NewExpression node)
            {
                // We are building a serializer specifically for an anonymous type based 
                // on serialization information collected from other serializers.
                // We cannot cache this because the compiler reuses the same anonymous type
                // definition in different contexts as long as they are structurally equatable.
                // As such, it might be that two different queries projecting the same shape
                // might need to be deserialized differently.
                var classMapType = typeof(BsonClassMap<>).MakeGenericType(node.Type);
                BsonClassMap classMap = (BsonClassMap)Activator.CreateInstance(classMapType);

                var properties = node.Type.GetProperties();
                var parameterToPropertyMap = from parameter in node.Constructor.GetParameters()
                                             join property in properties on parameter.Name equals property.Name
                                             select new { Parameter = parameter, Property = property };

                foreach (var parameterToProperty in parameterToPropertyMap)
                {
                    var argument = node.Arguments[parameterToProperty.Parameter.Position];
                    var field = argument as FieldExpression;
                    if (field == null)
                    {
                        var serializer = Build(argument);
                        var serializationInfo = new BsonSerializationInfo(parameterToProperty.Property.Name, serializer, parameterToProperty.Property.PropertyType);
                        field = new FieldExpression(
                            node.Arguments[parameterToProperty.Parameter.Position],
                            serializationInfo);
                    }

                    classMap.MapMember(parameterToProperty.Property)
                        .SetSerializer(field.SerializationInfo.Serializer)
                        .SetElementName(parameterToProperty.Property.Name);

                    //TODO: Need to set default value as well...
                }

                // Anonymous types are immutable and have all their values passed in via a ctor.
                classMap.MapConstructor(node.Constructor, properties.Select(x => x.Name).ToArray());
                classMap.Freeze();

                var serializerType = typeof(BsonClassMapSerializer<>).MakeGenericType(node.Type);
                return (IBsonSerializer)Activator.CreateInstance(serializerType, classMap);
            }
        }

        private class FieldNameReplacer : MongoExpressionVisitor
        {
            public static Expression Replace(Expression node, string oldName, string newName)
            {
                var replacer = new FieldNameReplacer(oldName, newName);
                return replacer.Visit(node);
            }

            private readonly string _oldName;
            private readonly string _newName;

            private FieldNameReplacer(string oldName, string newName)
            {
                _oldName = oldName;
                _newName = newName;
            }

            protected override Expression VisitDocument(DocumentExpression node)
            {
                if (node.SerializationInfo.ElementName.StartsWith(_oldName))
                {
                    return new DocumentExpression(
                        node.Expression,
                        node.SerializationInfo.WithNewName(GetReplacementName(node.SerializationInfo.ElementName)));
                }

                return base.VisitDocument(node);
            }

            protected override Expression VisitField(FieldExpression node)
            {
                if (node.SerializationInfo.ElementName.StartsWith(_oldName))
                {
                    return new FieldExpression(
                        node.Expression,
                        node.SerializationInfo.WithNewName(GetReplacementName(node.SerializationInfo.ElementName)));
                }

                return base.VisitField(node);
            }

            private string GetReplacementName(string elementName)
            {
                var suffix = elementName.Substring(_oldName.Length);
                return _newName + suffix;
            }
        }
    }
}
