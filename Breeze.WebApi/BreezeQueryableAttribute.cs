﻿using System;
using System.Collections;
using System.Linq;
using System.Linq.Expressions;
using System.Net.Http;
using System.Reflection;
using System.Web.Http;
using System.Web.Http.Filters;
using System.Web.Http.OData.Query;

namespace Breeze.WebApi {

  /// <summary>
  /// Used to parse and interpret OData like semantics on top of WebApi.
  /// </summary>
  /// <remarks>
  /// Remember to add it to the Filters for your configuration
  /// </remarks>
  [AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, Inherited = true, AllowMultiple = false)]
  public class BreezeQueryableAttribute : QueryableAttribute {

    public BreezeQueryableAttribute() {
      // because QueryableAttribute does not support Expand and Select
      this.AllowedQueryOptions = AllowedQueryOptions.Supported | AllowedQueryOptions.Expand | AllowedQueryOptions.Select;
    }

    public override void OnActionExecuting(System.Web.Http.Controllers.HttpActionContext actionContext) {
      base.OnActionExecuting(actionContext);
    }
        /// <summary>
    /// Called when the action is executed.
    /// </summary>
    /// <param name="actionExecutedContext">The action executed context.</param>
    public override void OnActionExecuted(HttpActionExecutedContext actionExecutedContext) {

      base.OnActionExecuted(actionExecutedContext);
      
      if (!actionExecutedContext.Response.IsSuccessStatusCode) {
        return;
      }

      object responseObject;
      if (!actionExecutedContext.Response.TryGetContentValue(out responseObject)) {
        return;
      }

      dynamic rQuery = null;
      var queryable = ApplySelectAndExpand(responseObject as IQueryable, actionExecutedContext.Request);
      if (queryable != null) {
        // if a select or expand was encountered we need to
        // execute the DbQueries here, so that any exceptions thrown can be properly returned.
        // if we wait to have the query executed within the serializer, some exceptions will not
        // serialize properly.
        rQuery = Enumerable.ToList((dynamic) queryable);
      } 

      Object tmp;
      actionExecutedContext.Request.Properties.TryGetValue("MS_InlineCount", out tmp);
      var inlineCount = (Int64?) tmp;
      
      if (rQuery!=null || inlineCount.HasValue) {
        if (rQuery == null) {
          rQuery = responseObject;
        }
        if (inlineCount.HasValue) {
          rQuery = new QueryResult() { Results = rQuery, InlineCount = inlineCount};
        }
        
        var formatter = ((dynamic) actionExecutedContext.Response.Content).Formatter;
        var oc = new ObjectContent(rQuery.GetType(), rQuery, formatter);
        actionExecutedContext.Response.Content = oc;
      } 

      
    }
    
    // all standard OData web api support is handled here ( except select and expand).
    // This method also handles nested orderby statements the the current ASP.NET web api does not yet support.
    public override IQueryable ApplyQuery(IQueryable queryable, ODataQueryOptions queryOptions) {
      IQueryable result;
      try {
        result = base.ApplyQuery(queryable, queryOptions);
      } catch (Exception) {
        result = ApplyExtendedOrderBy(queryable, queryOptions);
        if (result == null) {
          throw;
        }
      }
      
      return result;
    }

    private IQueryable ApplyExtendedOrderBy(IQueryable queryable, ODataQueryOptions queryOptions) {
      // if we see an extended order by we also need to process any skip/take operators as well.
      var orderByQueryString = queryOptions.RawValues.OrderBy;
      if (orderByQueryString == null || orderByQueryString.IndexOf("/") == -1) {
        return null;
      }

      var request = queryOptions.Request;
      var oldUri = request.RequestUri;
      var map = oldUri.ParseQueryString();
      var newQuery = map.Keys.Cast<String>()
                        .Where(k => (k != "$orderby") && (k != "$top") && (k != "$skip") )
                        .Select(k => k + "=" + map[k])
                        .ToAggregateString("&");

      var newUrl = oldUri.Scheme + "://" + oldUri.Authority + oldUri.AbsolutePath + "?" + newQuery;

      var newRequest = new HttpRequestMessage(request.Method, new Uri(newUrl));
      var newQo = new ODataQueryOptions(queryOptions.Context, newRequest);
      var result = base.ApplyQuery(queryable, newQo);

      var elementType = TypeFns.GetElementType(queryable.GetType());

      
      if (!string.IsNullOrWhiteSpace(orderByQueryString)) {
        var orderByClauses = orderByQueryString.Split(',').ToList();
        var isThenBy = false;
        orderByClauses.ForEach(obc => {
          var func = QueryBuilder.BuildOrderByFunc(isThenBy, elementType, obc);
          result = func(result);
          isThenBy = true;
        });
      }

      var skipQueryString = queryOptions.RawValues.Skip;
      if (!string.IsNullOrWhiteSpace(skipQueryString)) {
        var count = int.Parse(skipQueryString);
        var method = TypeFns.GetMethodByExample((IQueryable<String> q) => Queryable.Skip<String>(q, 999), elementType);
        var func = BuildIQueryableFunc(elementType, method, count);
        result = func(result);
      }

      var topQueryString = queryOptions.RawValues.Top;
      if (!string.IsNullOrWhiteSpace(topQueryString)) {
        var count = int.Parse(topQueryString);
        var method = TypeFns.GetMethodByExample((IQueryable<String> q) => Queryable.Take<String>(q, 999), elementType);
        var func = BuildIQueryableFunc(elementType, method, count);
        result = func(result);
      }

      request.Properties.Add("breeze_orderBy", true);
      return result;
    }

    private static Func<IQueryable, IQueryable> BuildIQueryableFunc<TArg>(Type instanceType, MethodInfo method, TArg parameter, Type queryableBaseType = null) {
      if (queryableBaseType == null) {
        queryableBaseType = typeof(IQueryable<>);
      }
      var paramExpr = Expression.Parameter(typeof(IQueryable));
      var queryableType = queryableBaseType.MakeGenericType(instanceType);
      var castParamExpr = Expression.Convert(paramExpr, queryableType);


      var callExpr = Expression.Call(method, castParamExpr, Expression.Constant(parameter));
      var castResultExpr = Expression.Convert(callExpr, typeof(IQueryable));
      var lambda = Expression.Lambda(castResultExpr, paramExpr);
      var func = (Func<IQueryable, IQueryable>)lambda.Compile();
      return func;
    }

    public IQueryable ApplySelectAndExpand(IQueryable queryable, HttpRequestMessage request ) {
      var result = queryable;
      var hasSelectOrExpand = false;      

      var map = request.RequestUri.ParseQueryString();

      var selectQueryString = map["$select"];
      if (!string.IsNullOrWhiteSpace(selectQueryString)) {
          result = ApplySelect(queryable, selectQueryString, request);
        hasSelectOrExpand = true;
      }

      var expandsQueryString = map["$expand"];
      if (!string.IsNullOrWhiteSpace(expandsQueryString)) {
        if (!string.IsNullOrWhiteSpace(selectQueryString)) {
          throw new Exception("Use of both 'expand' and 'select' in the same query is not currently supported");
        }
        result = ApplyExpand(queryable, expandsQueryString, request);
        hasSelectOrExpand = true;
      }

      return hasSelectOrExpand ? result : null;
      
    }

    /// <summary>
    /// Apply the select clause to the queryable
    /// </summary>
    /// <param name="queryable"></param>
    /// <param name="selectQueryString"></param>
    /// <param name="request">not used, but available to overriding methods</param>
    /// <returns></returns>
    public virtual IQueryable ApplySelect(IQueryable queryable, string selectQueryString, HttpRequestMessage request)
    {
        var selectClauses = selectQueryString.Split(',').Select(sc => sc.Replace('/', '.')).ToList();
        var elementType = TypeFns.GetElementType(queryable.GetType());
        var func = QueryBuilder.BuildSelectFunc(elementType, selectClauses);
        return func(queryable);
    }
    
    /// <summary>
    /// Apply to expands clause to the queryable
    /// </summary>
    /// <param name="queryable"></param>
    /// <param name="expandsQueryString"></param>
    /// <param name="request">not used, but available to overriding methods</param>
    /// <returns></returns>
    public virtual IQueryable ApplyExpand(IQueryable queryable, string expandsQueryString, HttpRequestMessage request)
    {
        expandsQueryString.Split(',').Select(s => s.Trim()).ToList().ForEach(expand =>
        {
            queryable = ((dynamic)queryable).Include(expand.Replace('/', '.'));
        });
        return queryable;
    }
      

  }

  public class QueryResult {
    public dynamic Results { get; set; }
    public Int64? InlineCount { get; set; }
  }
}


