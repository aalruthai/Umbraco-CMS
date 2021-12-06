// Copyright (c) Umbraco.
// See LICENSE for more details.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Notifications;

namespace Umbraco.Cms.Core.Events
{
    /// <content>
    /// Contains types and methods that allow publishing general notifications.
    /// </content>
    public partial class EventAggregator : IEventAggregator
    {
        private static readonly ConcurrentDictionary<Type, NotificationAsyncHandlerWrapper> s_notificationAsyncHandlers
            = new ConcurrentDictionary<Type, NotificationAsyncHandlerWrapper>();

        private static readonly ConcurrentDictionary<Type, NotificationHandlerWrapper> s_notificationHandlers
            = new ConcurrentDictionary<Type, NotificationHandlerWrapper>();

        private Task PublishNotificationAsync(INotification notification, CancellationToken cancellationToken = default)
        {
            Type notificationType = notification.GetType();
            NotificationAsyncHandlerWrapper asyncHandler = s_notificationAsyncHandlers.GetOrAdd(
                notificationType,
                t => (NotificationAsyncHandlerWrapper)Activator.CreateInstance(typeof(NotificationAsyncHandlerWrapperImpl<>).MakeGenericType(notificationType)));

            return asyncHandler.HandleAsync(notification, cancellationToken, _serviceFactory, PublishCoreAsync);
        }

        private void PublishNotification(INotification notification)
        {
            Type notificationType = notification.GetType();
            NotificationHandlerWrapper asyncHandler = s_notificationHandlers.GetOrAdd(
                notificationType,
                t => (NotificationHandlerWrapper)Activator.CreateInstance(typeof(NotificationHandlerWrapperImpl<>).MakeGenericType(notificationType)));

            asyncHandler.Handle(notification, _serviceFactory, PublishCore);
        }

        private async Task PublishCoreAsync(
            IEnumerable<Func<INotification, CancellationToken, Task>> allHandlers,
            INotification notification,
            CancellationToken cancellationToken)
        {
            foreach (Func<INotification, CancellationToken, Task> handler in allHandlers)
            {
                await handler(notification, cancellationToken).ConfigureAwait(false);
            }
        }

        private void PublishCore(
            IEnumerable<Action<INotification>> allHandlers,
            INotification notification)
        {
            foreach (Action<INotification> handler in allHandlers)
            {
                handler(notification);
            }
        }
    }

    internal abstract class NotificationHandlerWrapper
    {
        public abstract void Handle(
            INotification notification,
            ServiceFactory serviceFactory,
            Action<IEnumerable<Action<INotification>>, INotification> publish);
    }

    internal abstract class NotificationAsyncHandlerWrapper
    {
        public abstract Task HandleAsync(
            INotification notification,
            CancellationToken cancellationToken,
            ServiceFactory serviceFactory,
            Func<IEnumerable<Func<INotification, CancellationToken, Task>>, INotification, CancellationToken, Task> publish);
    }

    internal class NotificationAsyncHandlerWrapperImpl<TNotification> : NotificationAsyncHandlerWrapper
        where TNotification : INotification
    {
        /// <remarks>
        /// <para>
        /// Background - During v9 build we wanted an in-process message bus to facilitate removal of the old static event handlers. <br/>
        /// Instead of taking a dependency on MediatR we (the community) implemented our own using MediatR as inspiration.
        /// </para>
        ///
        /// <para>
        /// Some things worth knowing about MediatR.
        /// <list type="number">
        /// <item>All handlers are by default registered with transient lifetime, but can easily depend on services with state.</item>
        /// <item>Both the Mediatr instance and its handler resolver are registered scoped and as such it is always possible to depend on scoped services in a handler.</item>
        /// </list>
        /// </para>
        ///
        /// <para>
        /// Our EventAggregator started out registered with a transient lifetime but later (before initial release) the registration was changed to singleton, presumably
        /// because there are a lot of singleton services in Umbraco which like to publish notifications and it's a pain to use scoped services from a singleton.
        /// <br/>
        /// The problem with a singleton EventAggregator is it forces handlers to create a service scope and service locate any scoped services
        /// they wish to make use of e.g. a unit of work (think entity framework DBContext).
        /// </para>
        ///
        /// <para>
        /// Moving forwards (v10) it probably makes more sense to register EventAggregator scoped and create a scope as required in singletons that wish to publish notifications.
        /// But doing so now would mean an awful lot of service location to avoid breaking changes.
        /// <br/>
        /// For v9 we can do the next best thing which is to try use the HttpContextAccessor service provider (scoped per request) when available,
        /// and fallback creating a service scope otherwise.
        /// </para>
        ///
        /// <para>
        /// This is a slight improvement as we can depend on scoped services in a handler without service location and that scope is shared between publisher and subscriber during lifetime of a http request.
        /// However it isn't perfect, if a background thread creates a service scope and uses it to resolve an event aggregator and publish a notification
        /// that scope will not be the same scope that handlers are resolved from, so it is impossible to share scoped state between publisher and subscriber.
        /// </para>
        /// </remarks>
        public override Task HandleAsync(
            INotification notification,
            CancellationToken cancellationToken,
            ServiceFactory serviceFactory,
            Func<IEnumerable<Func<INotification, CancellationToken, Task>>, INotification, CancellationToken, Task> publish)
        {
            // Try get a scoped service provider from HttpContextAccessor, we will use this if present.
            IScopedServiceProvider scopedServiceProvider = serviceFactory.GetInstance<IScopedServiceProvider>();

            // As a fallback, create a new service scope and ensure it's disposed when it goes out of scope.
            IServiceScopeFactory scopeFactory = serviceFactory.GetInstance<IServiceScopeFactory>();
            using IServiceScope scope = scopeFactory.CreateScope();

            // Use best service provider available for resolving handlers.
            IServiceProvider container = scopedServiceProvider.ServiceProvider ?? scope.ServiceProvider;

            IEnumerable<Func<INotification, CancellationToken, Task>> handlers = container
                .GetServices<INotificationAsyncHandler<TNotification>>()
                .Select(x => new Func<INotification, CancellationToken, Task>(
                    (theNotification, theToken) =>
                        x.HandleAsync((TNotification)theNotification, theToken)));

            return publish(handlers, notification, cancellationToken);
        }
    }

    internal class NotificationHandlerWrapperImpl<TNotification> : NotificationHandlerWrapper
        where TNotification : INotification
    {
        /// <remarks>
        /// See remarks on <see cref="NotificationAsyncHandlerWrapperImpl{T}.HandleAsync"/> for explanation on
        /// what's going on with the IServiceProvider stuff here.
        /// </remarks>
        public override void Handle(
            INotification notification,
            ServiceFactory serviceFactory,
            Action<IEnumerable<Action<INotification>>, INotification> publish)
        {
            // Try get a scoped service provider from HttpContextAccessor, we will use this if present.
            IScopedServiceProvider scopedServiceProvider = serviceFactory.GetInstance<IScopedServiceProvider>();

            // As a fallback, create a new service scope and ensure it's disposed when it goes out of scope.
            IServiceScopeFactory scopeFactory = serviceFactory.GetInstance<IServiceScopeFactory>();
            using IServiceScope scope = scopeFactory.CreateScope();

            // Use best service provider available for resolving handlers.
            IServiceProvider container = scopedServiceProvider.ServiceProvider ?? scope.ServiceProvider;

            IEnumerable<Action<INotification>> handlers = container
                .GetServices<INotificationHandler<TNotification>>()
                .Select(x => new Action<INotification>(
                    (theNotification) =>
                        x.Handle((TNotification)theNotification)));

            publish(handlers, notification);
        }
    }
}
