﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace CodeJam.Reflection
{
	public class TypeAccessor<T> : TypeAccessor
	{
		static TypeAccessor()
		{
			// Create Instance.
			//
			var type = typeof(T);

			if (type.IsValueType)
			{
				_createInstance = () => default(T);
			}
			else
			{
				var ctor = type.IsAbstract ? null : type.GetDefaultConstructor();

				if (ctor == null)
				{
					Expression<Func<T>> mi;

					if (type.IsAbstract) mi = () => ThrowAbstractException();
					else                 mi = () => ThrowException();

					var body = Expression.Call(null, ((MethodCallExpression)mi.Body).Method);

					_createInstance = Expression.Lambda<Func<T>>(body).Compile();
				}
				else
				{
					_createInstance = Expression.Lambda<Func<T>>(Expression.New(ctor)).Compile();
				}
			}

			foreach (var memberInfo in type.GetMembers(BindingFlags.Instance | BindingFlags.Public))
			{
				if (memberInfo.MemberType == MemberTypes.Field || 
					memberInfo.MemberType == MemberTypes.Property && ((PropertyInfo)memberInfo).GetIndexParameters().Length == 0)
				{
					_members.Add(memberInfo);
				}
			}

			// Add explicit iterface implementation properties support
			// Or maybe we should support all private fields/properties?
			//
			var interfaceMethods = type.GetInterfaces().SelectMany(ti => type.GetInterfaceMap(ti).TargetMethods).ToList();

			if (interfaceMethods.Count > 0)
			{
				foreach (var pi in type.GetProperties(BindingFlags.NonPublic | BindingFlags.Instance))
				{
					if (pi.GetIndexParameters().Length == 0)
					{
						var getMethod = pi.GetGetMethod(true);
						var setMethod = pi.GetSetMethod(true);

						if ((getMethod == null || interfaceMethods.Contains(getMethod)) &&
							(setMethod == null || interfaceMethods.Contains(setMethod)))
						{
							_members.Add(pi);
						}
					}
				}
			}
		}

		static T ThrowException()
		{
			throw new InvalidOperationException($"The '{typeof(T).FullName}' type must have default or init constructor.");
		}

		static T ThrowAbstractException()
		{
			throw new InvalidOperationException($"Cant create an instance of abstract class '{typeof(T).FullName}'.");
		}

		static readonly List<MemberInfo> _members = new List<MemberInfo>();

		internal TypeAccessor()
		{
			foreach (var member in _members)
				AddMember(new MemberAccessor(this, member));
		}

		static readonly Func<T> _createInstance;
		public override object CreateInstance() => _createInstance();

		public T Create() => _createInstance();

		public override Type Type => typeof(T);
	}
}