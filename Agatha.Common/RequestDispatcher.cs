using System;
using System.Collections.Generic;
using System.Linq;
using Agatha.Common.Caching;

namespace Agatha.Common
{
	public interface IRequestDispatcher : IDisposable
	{
		IEnumerable<Response> Responses { get; }

		void Add(Request request);
		void Add(params Request[] requestsToAdd);
		void Add(string key, Request request);
		void Add<TRequest>(Action<TRequest> action) where TRequest : Request, new();
		void Send(params OneWayRequest[] oneWayRequests);
		bool HasResponse<TResponse>() where TResponse : Response;
		TResponse Get<TResponse>() where TResponse : Response;
		TResponse Get<TResponse>(string key) where TResponse : Response;
		TResponse Get<TResponse>(Request request) where TResponse : Response;
		void Clear();
	}

	// TODO: make sure that OneWayRequests can't be added through the Add methods

	public class RequestDispatcher : Disposable, IRequestDispatcher
	{
		private readonly IRequestProcessor requestProcessor;
		private readonly ICacheManager cacheManager;

		private Dictionary<string, Type> keyToTypes;
		protected Dictionary<string, int> keyToResultPositions;
		private List<Request> requests;
		private Response[] responses;

		public RequestDispatcher(IRequestProcessor requestProcessor, ICacheManager cacheManager)
		{
			this.requestProcessor = requestProcessor;
			this.cacheManager = cacheManager;
			InitializeState();
		}

		private void InitializeState()
		{
			requests = new List<Request>();
			responses = null;
			keyToTypes = new Dictionary<string, Type>();
			keyToResultPositions = new Dictionary<string, int>();
		}

		public IEnumerable<Request> SentRequests
		{
			get { return requests; }
		}

		public IEnumerable<Response> Responses
		{
			get
			{
				SendRequestsIfNecessary();
				return responses;
			}
		}

		public virtual void Add(params Request[] requestsToAdd)
		{
			foreach (var request in requestsToAdd)
			{
				Add(request);
			}
		}

		public virtual void Add<TRequest>(Action<TRequest> action) where TRequest : Request, new()
		{
			var request = new TRequest();
			action(request);
			Add(request);
		}

		public virtual void Add(Request request)
		{
			AddRequest(request, false);
		}

		public virtual void Add(string key, Request request)
		{
            if (keyToTypes.Keys.Contains(key))
                throw new InvalidOperationException(
                    String.Format("A request has already been added using the key '{0}'.", key));
			AddRequest(request, true);
			keyToTypes[key] = request.GetType();
			keyToResultPositions[key] = requests.Count - 1;
		}

		public virtual void Send(params OneWayRequest[] oneWayRequests)
		{
			BeforeSendingRequests(oneWayRequests);
			requestProcessor.ProcessOneWayRequests(oneWayRequests);
			AfterSendingRequests(oneWayRequests);
		}

		public virtual bool HasResponse<TResponse>() where TResponse : Response
		{
			SendRequestsIfNecessary();
			return responses.OfType<TResponse>().Count() > 0;
		}

		public virtual TResponse Get<TResponse>() where TResponse : Response
		{
			SendRequestsIfNecessary();
			return responses.OfType<TResponse>().Single();
		}

		public virtual TResponse Get<TResponse>(string key) where TResponse : Response
		{
			SendRequestsIfNecessary();
			return (TResponse)responses[keyToResultPositions[key]];
		}

		public virtual TResponse Get<TResponse>(Request request) where TResponse : Response
		{
			Add(request);
			return Get<TResponse>();
		}

		public virtual void Clear()
		{
			InitializeState();
		}

		protected override void DisposeManagedResources()
		{
			if (requestProcessor != null) requestProcessor.Dispose();
		}

		protected virtual Response[] GetResponses(params Request[] requestsToProcess)
		{
			BeforeSendingRequests(requestsToProcess);

			var tempResponseArray = new Response[requestsToProcess.Length];
			var requestsToSend = new List<Request>(requestsToProcess);

			GetCachedResponsesAndRemoveThoseRequests(requestsToProcess, tempResponseArray, requestsToSend);
			var requestsToSendAsArray = requestsToSend.ToArray();

			if (requestsToSend.Count > 0)
			{
				var receivedResponses = requestProcessor.Process(requestsToSendAsArray);
				AddCacheableResponsesToCache(receivedResponses, requestsToSendAsArray);
				PutReceivedResponsesInTempResponseArray(tempResponseArray, receivedResponses);
			}

			AfterSendingRequests(requestsToProcess);
			BeforeReturningResponses(tempResponseArray);
			return tempResponseArray;
		}

		private void GetCachedResponsesAndRemoveThoseRequests(Request[] requestsToProcess, Response[] tempResponseArray, List<Request> requestsToSend)
		{
			for (int i = 0; i < requestsToProcess.Length; i++)
			{
				var request = requestsToProcess[i];

				if (cacheManager.IsCachingEnabledFor(request.GetType()))
				{
					var cachedResponse = cacheManager.GetCachedResponseFor(request);

					if (cachedResponse != null)
					{
						tempResponseArray[i] = cachedResponse;
						requestsToSend.Remove(request);
					}
				}
			}
		}

		private void AddCacheableResponsesToCache(Response[] receivedResponses, Request[] requestsToSend)
		{
			for (int i = 0; i < receivedResponses.Length; i++)
			{
				if (receivedResponses[i].ExceptionType == ExceptionType.None && cacheManager.IsCachingEnabledFor(requestsToSend[i].GetType()))
				{
					cacheManager.StoreInCache(requestsToSend[i], receivedResponses[i]);
				}
			}
		}

		private void PutReceivedResponsesInTempResponseArray(Response[] tempResponseArray, Response[] receivedResponses) 
		{
			int takeIndex = 0;

			for (int i = 0; i < tempResponseArray.Length; i++)
			{
				if (tempResponseArray[i] == null)
				{
					tempResponseArray[i] = receivedResponses[takeIndex++];
				}
			}
		}

		protected virtual void BeforeSendingRequests(IEnumerable<Request> requestsToProcess) {}
		protected virtual void AfterSendingRequests(IEnumerable<Request> sentRequests) {}
		protected virtual void BeforeReturningResponses(IEnumerable<Response> receivedResponses) {}

		private void SendRequestsIfNecessary()
		{
			if (responses == null)
			{
				responses = GetResponses(requests.ToArray());
				DealWithPossibleExceptions(responses);
			}
		}

		private void DealWithPossibleExceptions(IEnumerable<Response> responsesToCheck)
		{
			foreach (var response in responsesToCheck)
			{
				if (response.ExceptionType == ExceptionType.Security)
				{
					DealWithSecurityException(response.Exception);
				}

				if (response.ExceptionType == ExceptionType.Unknown)
				{
					DealWithUnknownException(response.Exception);
				}
			}
		}

		protected virtual void DealWithUnknownException(ExceptionInfo exception) { }

		protected virtual void DealWithSecurityException(ExceptionInfo exceptionDetail) { }

		private void AddRequest(Request request, bool wasAddedWithKey)
		{
			Type requestType = request.GetType();

			if (RequestTypeIsAlreadyPresent(requestType) &&
				(RequestTypeIsNotAssociatedWithKey(requestType) || !wasAddedWithKey))
			{
				throw new InvalidOperationException(String.Format("A request of type {0} has already been added. "
																  + "Please add requests of the same type with a different key.", requestType.FullName));
			}

			requests.Add(request);
		}

		private bool RequestTypeIsNotAssociatedWithKey(Type requestType)
		{
			return !keyToTypes.Values.Contains(requestType);
		}

		private bool RequestTypeIsAlreadyPresent(Type requestType)
		{
			return requests.Count(r => r.GetType().Equals(requestType)) > 0;
		}
	}
}