﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace BLToolkit.Data.Linq.Parser
{
	using BLToolkit.Linq;
	using Data.Sql;
	using Reflection;

	// This class implements double functionality (scalar and member type selects)
	// and could be implemented as two different classes.
	// But the class means to have a lot of inheritors, and functionality of the inheritors
	// will be doubled as well. So lets double it once here.
	//
	public class SelectContext : IParseContext
	{
		#region Init

		public IParseContext[]  Sequence { get; set; }
		public LambdaExpression Lambda   { get; set; }
		public Expression       Body     { get; set; }
		public ExpressionParser Parser   { get; private set; }
		public SqlQuery         SqlQuery { get; set; }
		public IParseContext    Parent   { get; set; }
		public bool             IsScalar { get; private set; }

		Expression IParseContext.Expression { get { return Lambda; } }

		bool _isGroupBySubquery;

		public readonly Dictionary<MemberInfo,Expression> Members = new Dictionary<MemberInfo,Expression>();

		public SelectContext(LambdaExpression lambda, params IParseContext[] sequences)
		{
			Sequence = sequences;
			Parser   = sequences[0].Parser;
			Lambda   = lambda;
			Body     = lambda.Body.Unwrap();
			SqlQuery = sequences[0].SqlQuery;

			foreach (var context in Sequence)
				context.Parent = this;

			switch (Body.NodeType)
			{
				// .Select(p => new { ... })
				//
				case ExpressionType.New        :
					{
						var expr = (NewExpression)Body;

// ReSharper disable ConditionIsAlwaysTrueOrFalse
// ReSharper disable HeuristicUnreachableCode
						if (expr.Members == null)
							return;
// ReSharper restore HeuristicUnreachableCode
// ReSharper restore ConditionIsAlwaysTrueOrFalse

						for (var i = 0; i < expr.Members.Count; i++)
						{
							var member = expr.Members[i];

							Members.Add(member, expr.Arguments[i]);

							if (member is MethodInfo)
								Members.Add(TypeHelper.GetPropertyByMethod((MethodInfo)member), expr.Arguments[i]);
						}

						break;
					}

				// .Select(p => new MyObject { ... })
				//
				case ExpressionType.MemberInit :
					{
						var expr = (MemberInitExpression)Body;

						foreach (var binding in expr.Bindings)
						{
							if (binding is MemberAssignment)
							{
								var ma = (MemberAssignment)binding;

								Members.Add(binding.Member, ma.Expression);

								if (binding.Member is MethodInfo)
									Members.Add(TypeHelper.GetPropertyByMethod((MethodInfo)binding.Member), ma.Expression);
							}
							else
								throw new InvalidOperationException();
						}

						_isGroupBySubquery =
							Body.Type.IsGenericType &&
							Body.Type.GetGenericTypeDefinition() == typeof(ExpressionParser.GroupSubQuery<,>);

						break;
					}

				// .Select(p => everything else)
				//
				default                        :
					IsScalar = true;
					break;
			}
		}

		#endregion

		#region BuildQuery

		public virtual void BuildQuery<T>(Query<T> query, ParameterExpression queryParameter)
		{
			var expr = BuildExpression(null, 0);

			var mapper = Expression.Lambda<Func<QueryContext,IDataContext,IDataReader,Expression,object[],T>>(
				expr, new []
				{
					ExpressionParser.ContextParam,
					ExpressionParser.DataContextParam,
					ExpressionParser.DataReaderParam,
					ExpressionParser.ExpressionParam,
					ExpressionParser.ParametersParam,
				});

			query.SetQuery(mapper.Compile());
		}

		#endregion

		#region BuildExpression

		public virtual Expression BuildExpression(Expression expression, int level)
		{
			if (expression == null)
				return Parser.BuildExpression(this, Body);

			var levelExpression = expression.GetLevelExpression(level);

			if (IsScalar)
			{
				if (Body.NodeType == ExpressionType.Parameter)
				{
					if (level == 0)
					{
						var sequence = GetSequence(Body, 0);

						return expression == Body ?
							sequence.BuildExpression(null,       0) :
							sequence.BuildExpression(expression, 1);
					}

					levelExpression = expression.GetLevelExpression(level - 1);

					var parseExpression = GetParseExpression(expression, levelExpression, Body);

					return BuildExpression(parseExpression, 0);
				}

				if (level == 0)
				{
					if (levelExpression != expression)
						return GetSequence(expression, level).BuildExpression(expression, level + 1);

					if (IsSubQuery() && IsExpression(null, 0, RequestFor.Expression))
					{
						var idx = ConvertToIndex(expression, level, ConvertFlags.Field).Single();

						idx = Parent == null ? idx : Parent.ConvertToParentIndex(idx, this);

						return Parser.BuildSql(expression.Type, idx);
					}

					return GetSequence(expression, level).BuildExpression(null, 0);
				}

				var root = Body.GetRootObject();

				if (root.NodeType == ExpressionType.Parameter)
				{
					levelExpression = expression.GetLevelExpression(level - 1);
					var parseExpression = GetParseExpression(expression, levelExpression, Body);

					return BuildExpression(parseExpression, 0);
				}

				//if (levelExpression != expression)
				//	return GetSequence(expression, level).BuildExpression(expression, level + 1);
			}
			else
			{
				var sequence  = GetSequence(expression, level);
				var parameter = Lambda.Parameters[Sequence.Length == 0 ? 0 : Array.IndexOf(Sequence, sequence)];

				if (level == 0)
					return levelExpression == expression ?
						sequence.BuildExpression(null,       0) :
						sequence.BuildExpression(expression, level + 1);

				switch (levelExpression.NodeType)
				{
					case ExpressionType.MemberAccess :
						{
							var memberExpression = Members[((MemberExpression)levelExpression).Member];

							if (levelExpression == expression)
							{
								if (IsSubQuery())
								{
									switch (memberExpression.NodeType)
									{
										case ExpressionType.New        :
										case ExpressionType.MemberInit :
											return memberExpression.Convert(e =>
											{
												if (e != memberExpression)
												{
													if (!sequence.IsExpression(e, 0, RequestFor.Query) &&
													    !sequence.IsExpression(e, 0, RequestFor.Field))
													{
														var idx = ConvertToIndex(e, 0, ConvertFlags.Field).Single();

														idx = Parent == null ? idx : Parent.ConvertToParentIndex(idx, this);

														return Parser.BuildSql(e.Type, idx);
													}

													return Parser.BuildExpression(this, e);
												}

												return e;
											});
									}

									if (!sequence.IsExpression(memberExpression, 0, RequestFor.Query) &&
									    !sequence.IsExpression(memberExpression, 0, RequestFor.Field))
									{
										var idx = ConvertToIndex(expression, level, ConvertFlags.Field).Single();

										idx = Parent == null ? idx : Parent.ConvertToParentIndex(idx, this);

										return Parser.BuildSql(expression.Type, idx);
									}
								}

								return Parser.BuildExpression(this, memberExpression);
							}

							switch (memberExpression.NodeType)
							{
								case ExpressionType.Parameter  :
									if (memberExpression == parameter)
										return sequence.BuildExpression(expression, level + 1);
									break;

								case ExpressionType.New        :
								case ExpressionType.MemberInit :
									{
										var memberMemberExpresion = expression.GetLevelExpression(level + 1);

										break;
									}
							}

							var expr = expression.Convert(ex => ex == levelExpression ? memberExpression : ex);

							return sequence.BuildExpression(expr, 1);
						}

					case ExpressionType.Parameter :

						//if (levelExpression == expression)
							break;
						//return Sequence.BuildExpression(expression, level + 1);
				}
			}

			throw new NotImplementedException();
		}

		#endregion

		#region ConvertToSql

		readonly Dictionary<MemberInfo,ISqlExpression[]> _sql = new Dictionary<MemberInfo,ISqlExpression[]>();

		public virtual ISqlExpression[] ConvertToSql(Expression expression, int level, ConvertFlags flags)
		{
			if (IsScalar)
			{
				switch (flags)
				{
					case ConvertFlags.Field :
					case ConvertFlags.Key   :
					case ConvertFlags.All   :
						{
							if (expression == null)
								return ConvertToSql(Body, 0, flags);

							if (Body.NodeType == ExpressionType.Parameter)
							{
								if (level == 0)
								{
									var sequence = GetSequence(Body, 0);

									return expression == Body ?
										sequence.ConvertToSql(null,       0, flags) :
										sequence.ConvertToSql(expression, 1, flags);
								}

								var levelExpression = expression.GetLevelExpression(level - 1);
								var parseExpression = GetParseExpression(expression, levelExpression, Body);

								return ConvertToSql(parseExpression, 0, flags);
							}

							if (level == 0)
							{
								var levelExpression = expression.GetLevelExpression(level);

								if (levelExpression != expression)
								{
									var flag = flags;

									if (flags != ConvertFlags.Field && IsExpression(expression, level, RequestFor.Field))
										flag = ConvertFlags.Field;

									return GetSequence(expression, level).ConvertToSql(expression, level + 1, flag);
								}

								switch (Body.NodeType)
								{
									case ExpressionType.MemberAccess :
									case ExpressionType.Call         :
										break;
										//return GetSequence(expression, level).IsExpression(Body, 1, requestFlag);
									default                          : return new[] { Parser.ParseExpression(this, expression) };
								}
							}
							else
							{
								var root = Body.GetRootObject();

								if (root.NodeType == ExpressionType.Parameter)
								{
									var levelExpression = expression.GetLevelExpression(level - 1);
									var parseExpression = GetParseExpression(expression, levelExpression, Body);

									return ConvertToSql(parseExpression, 0, flags);
								}
							}

							break;
						}

						/*
						if (Body.NodeType == ExpressionType.Parameter)
						{
							if (expression == null)
								return GetSequence(Body, 0).ConvertToSql(null, 0, flags);

							var levelExpression = expression.GetLevelExpression(level);

							if (levelExpression == expression)
								return GetSequence(expression, level).ConvertToSql(null, 0, flags);
						}
						else
						{
							if (expression == null)
								return new[] { Parser.ParseExpression(this, Body) };

							if (level == 0)
								return GetSequence(expression, level).ConvertToSql(expression, level + 1, flags);
						}

						break;
						*/
				}
			}
			else
			{
				switch (flags)
				{
					case ConvertFlags.All   :
					case ConvertFlags.Key   :
					case ConvertFlags.Field :
						{
							if (level != 0)
							{
								var levelExpression = expression.GetLevelExpression(level);

								switch (levelExpression.NodeType)
								{
									case ExpressionType.MemberAccess :
										{
											var member = ((MemberExpression)levelExpression).Member;

											if (levelExpression == expression)
											{
												ISqlExpression[] sql;

												if (!_sql.TryGetValue(member, out sql))
												{
													sql = ParseExpressions(Members[member], flags);
													_sql.Add(member, sql);
												}

												return sql;
											}

											var memberExpression = Members[member];
											var parseExpression  = GetParseExpression(expression, levelExpression, memberExpression);

											return ParseExpressions(parseExpression, flags);
										}

									case ExpressionType.Parameter:

										if (levelExpression != expression)
											return GetSequence(expression, level).ConvertToSql(expression, level + 1, flags);
										break;
								}
							}

							if (level == 0)
							{
								if (expression == null)
								{
									if (flags != ConvertFlags.Field)
									{
										var q =
											from m in Members.Values.Distinct()
											select ConvertMember(m, flags) into mm
											from m in mm
											select m;

										return q.ToArray();
									}
								}
								else
								{
									if (expression.NodeType == ExpressionType.Parameter)
									{
										var levelExpression = expression.GetLevelExpression(level);

										if (levelExpression == expression)
											return GetSequence(expression, level).ConvertToSql(null, 0, flags);
									}
									else
									{
										return GetSequence(expression, level).ConvertToSql(expression, level + 1, flags);
									}
								}
							}

							break;
						}
				}
			}

			throw new NotImplementedException();
		}

		ISqlExpression[] ConvertMember(Expression expression, ConvertFlags flags)
		{
			switch (expression.NodeType)
			{
				case ExpressionType.MemberAccess :
				case ExpressionType.Parameter :
					if (IsExpression(expression, 0, RequestFor.Field))
						flags = ConvertFlags.Field;
					return ConvertToSql(expression, 0, flags);
			}

			return ParseExpressions(expression, flags);
		}

		ISqlExpression[] ParseExpressions(Expression expression, ConvertFlags flags)
		{
			return Parser.ParseExpressions(this, expression, flags)
				.Select(_ => CheckExpression(_))
				.ToArray();
		}

		ISqlExpression CheckExpression(ISqlExpression expression)
		{
			if (expression is SqlQuery.SearchCondition)
			{
				expression = Parser.Convert(this, new SqlFunction(typeof(bool), "CASE", expression, new SqlValue(true), new SqlValue(false)));
			}

			return expression;
		}

		#endregion

		#region ConvertToIndex

		readonly Dictionary<Tuple<MemberInfo,ConvertFlags>,int[]> _memberIndex = new Dictionary<Tuple<MemberInfo,ConvertFlags>,int[]>();

		int[] _scalarIndex;

		public virtual int[] ConvertToIndex(Expression expression, int level, ConvertFlags flags)
		{
			if (IsScalar)
			{
				if (expression == null)
				{
					if (_scalarIndex == null)
						_scalarIndex = ConvertToSql(expression, 0, flags).Select(_ => GetIndex(_)).ToArray();
					return _scalarIndex;
				}

				switch (flags)
				{
					case ConvertFlags.Field :
					case ConvertFlags.All   : return GetSequence(expression, level).ConvertToIndex(expression, level + 1, flags);
				}
			}
			else
			{
				if (expression == null)
				{
					switch (flags)
					{
						case ConvertFlags.Field : throw new NotImplementedException();
						case ConvertFlags.Key   :
						case ConvertFlags.All   :
							{
								var p = Expression.Parameter(Body.Type, "p");
								var q =
									from m in Members.Keys
									where !(m is MethodInfo)
									select ConvertToIndex(Expression.MakeMemberAccess(p, m), 1, flags) into mm
									from m in mm
									select m;

								return q.ToArray();
							}
					}
				}

				switch (flags)
				{
					case ConvertFlags.All   :
					case ConvertFlags.Key   :
					case ConvertFlags.Field :
						{
							if (level == 0)
								return Parser.ParseExpressions(this, expression, flags).Select(s => GetIndex(s)).ToArray();

							var levelExpression = expression.GetLevelExpression(level);

							switch (levelExpression.NodeType)
							{
								case ExpressionType.MemberAccess :
									{
										if (levelExpression == expression)
										{
											var member = Tuple.Create(((MemberExpression)levelExpression).Member, flags);

											int[] idx;

											if (!_memberIndex.TryGetValue(member, out idx))
											{
												var sql = ConvertToSql(expression, level, flags);

												if (flags == ConvertFlags.Field && sql.Length != 1)
													throw new InvalidOperationException();

												idx = sql.Select(s => GetIndex(s)).ToArray();

												_memberIndex.Add(member, idx);
											}

											return idx;
										}

										return GetSequence(expression, level).ConvertToIndex(expression, level + 1, flags);
									}

								case ExpressionType.Parameter:

									if (levelExpression != expression)
										return GetSequence(expression, level).ConvertToIndex(expression, level + 1, flags);
									break;
							}

							break;
						}
				}
			}

			throw new NotImplementedException();
		}

		int GetIndex(ISqlExpression sql)
		{
			return SqlQuery.Select.Add(sql);
		}

		#endregion

		#region IsExpression

		public virtual bool IsExpression(Expression expression, int level, RequestFor requestFlag)
		{
			switch (requestFlag)
			{
				case RequestFor.SubQuery    : return false;
				case RequestFor.Root        :
					return Sequence.Length == 1 ?
						expression == Lambda.Parameters[0] :
						Lambda.Parameters.Any(p => p == expression);
			}

			if (IsScalar)
			{
				switch (requestFlag)
				{
					default                     : return false;
					case RequestFor.Association :
					case RequestFor.Field       :
					case RequestFor.Expression  :
					case RequestFor.Query       :
						{
							if (expression == null)
								return IsExpression(Body, 0, requestFlag);

							if (Body.NodeType == ExpressionType.Parameter)
							{
								if (level == 0)
								{
									var sequence = GetSequence(Body, 0);

									return expression == Body ?
										sequence.IsExpression(null,       0, requestFlag) :
										sequence.IsExpression(expression, 1, requestFlag);
								}

								var levelExpression = expression.GetLevelExpression(level - 1);
								var parseExpression = GetParseExpression(expression, levelExpression, Body);

								return IsExpression(parseExpression, 0, requestFlag);
							}

							if (level == 0)
							{
								var levelExpression = expression.GetLevelExpression(level);

								if (levelExpression != expression)
									return GetSequence(expression, level).IsExpression(expression, level + 1, requestFlag);

								switch (Body.NodeType)
								{
									case ExpressionType.MemberAccess :
									case ExpressionType.Call         : return GetSequence(expression, level).IsExpression(null, 0, requestFlag);
									default                          : return requestFlag == RequestFor.Expression;
								}
							}
							else
							{
								var root = Body.GetRootObject();

								if (root.NodeType == ExpressionType.Parameter)
								{
									var levelExpression = expression.GetLevelExpression(level - 1);
									var parseExpression = GetParseExpression(expression, levelExpression, Body);

									return IsExpression(parseExpression, 0, requestFlag);
								}
							}

							break;
						}
				}
			}
			else
			{
				switch (requestFlag)
				{
					default                     : return false;
					case RequestFor.Association :
					case RequestFor.Field       :
					case RequestFor.Expression  :
					case RequestFor.Query       :
						{
							if (expression == null)
							{
								if (requestFlag == RequestFor.Expression)
									return Members.Values.Any(member => IsExpression(member, 0, requestFlag));

								return requestFlag == RequestFor.Query;
							}

							var levelExpression = expression.GetLevelExpression(level);

							switch (levelExpression.NodeType)
							{
								case ExpressionType.MemberAccess :
									{
										var memberExpression = Members[((MemberExpression)levelExpression).Member];
										var parseExpression  = GetParseExpression(expression, levelExpression, memberExpression);

										var sequence  = GetSequence(expression, level);
										var parameter = Lambda.Parameters[Sequence.Length == 0 ? 0 : Array.IndexOf(Sequence, sequence)];

										if (memberExpression == parameter && levelExpression == expression)
											return sequence.IsExpression(null, 0, requestFlag);

										switch (memberExpression.NodeType)
										{
											case ExpressionType.MemberAccess :
											case ExpressionType.Parameter    :
											case ExpressionType.Call         : return sequence.IsExpression(parseExpression, 1, requestFlag);
											case ExpressionType.New          :
											case ExpressionType.MemberInit   : return requestFlag == RequestFor.Query;
											default                          : return requestFlag == RequestFor.Expression;
										}
									}

								case ExpressionType.Parameter    :
									{
										var sequence  = GetSequence(expression, level);
										var parameter = Lambda.Parameters[Sequence.Length == 0 ? 0 : Array.IndexOf(Sequence, sequence)];

										if (levelExpression == expression)
										{
											if (levelExpression == parameter)
												return sequence.IsExpression(null, 0, requestFlag);
										}
										else if (level == 0)
											return sequence.IsExpression(expression, 1, requestFlag);

										break;
									}

								case ExpressionType.New         :
								case ExpressionType.MemberInit  : return requestFlag == RequestFor.Query;
								default                         : return requestFlag == RequestFor.Expression;
							}

							break;
						}
				}
			}

			throw new NotImplementedException();
		}

		#endregion

		#region GetContext

		public virtual IParseContext GetContext(Expression expression, int level, SqlQuery currentSql)
		{
			return GetSequence(expression, level).GetContext(expression, level + 1, currentSql);
		}

		#endregion

		#region ConvertToParentIndex

		public virtual int ConvertToParentIndex(int index, IParseContext context)
		{
			return Parent == null ? index : Parent.ConvertToParentIndex(index, this);
		}

		#endregion

		#region SetAlias

		public virtual void SetAlias(string alias)
		{
		}

		#endregion

		#region Helpers

		protected bool IsSubQuery()
		{
			for (var p = Parent; p != null; p = p.Parent)
				if (p.IsExpression(null, 0, RequestFor.SubQuery))
					return true;
			return false;
		}

		IParseContext GetSequence(Expression expression, int level)
		{
			if (Sequence.Length == 1)
				return Sequence[0];

			var levelExpression = expression.GetLevelExpression(level);

			if (IsScalar)
			{
				var root =  Body.GetRootObject();

				if (root.NodeType == ExpressionType.Parameter)
					for (int i = 0; i < Lambda.Parameters.Count; i++)
						if (root == Lambda.Parameters[i])
							return Sequence[i];
			}
			else
			{
				switch (levelExpression.NodeType)
				{
					case ExpressionType.MemberAccess :
						{
							var memberExpression = Members[((MemberExpression)levelExpression).Member];
							var root             =  memberExpression.GetRootObject();

							for (int i = 0; i < Lambda.Parameters.Count; i++)
								if (root == Lambda.Parameters[i])
									return Sequence[i];

							break;
						}

					case ExpressionType.Parameter :
						{
							var root =  expression.GetRootObject();

							if (levelExpression == root)
							{
								for (int i = 0; i < Lambda.Parameters.Count; i++)
									if (levelExpression == Lambda.Parameters[i])
										return Sequence[i];
							}

							break;
						}
				}
			}

			throw new NotImplementedException();
		}

		static Expression GetParseExpression(Expression expression, Expression levelExpression, Expression memberExpression)
		{
			return levelExpression != expression ?
				expression.Convert(ex => ex == levelExpression ? memberExpression : ex) :
				memberExpression;
		}

		#endregion
	}
}
