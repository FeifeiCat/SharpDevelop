﻿// Copyright (c) AlphaSierraPapa for the SharpDevelop Team (for details please see \doc\copyright.txt)
// This code is distributed under the GNU LGPL (for details please see \doc\license.txt)

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Runtime.Remoting.Messaging;
using System.Web.Services.Description;
using System.Web.Services.Discovery;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;

using ICSharpCode.Core.Presentation;
using ICSharpCode.SharpDevelop.Project;
using ICSharpCode.SharpDevelop.Project.Commands;
using ICSharpCode.SharpDevelop.Widgets;

namespace ICSharpCode.SharpDevelop.Gui.Dialogs.ReferenceDialog.ServiceReference
{
	public class AddServiceReferenceViewModel : ViewModelBase
	{
		string header = "To see a list of available services on a specific server, enter a service URL and click Go.";
		string noUrl = "Please enter the address of the Service.";
		string title =  "Add Service Reference";
		string waitMessage = "Please wait....";
		string defaultNameSpace;
		string serviceDescriptionMessage;
		string namespacePrefix = String.Empty;
		
		ObservableCollection<ImageAndDescription> twoValues;
		
		ServiceReferenceUrlHistory urlHistory = new ServiceReferenceUrlHistory();
		string selectedService;
		IProject project;
		ServiceReferenceGenerator serviceGenerator;
		List<CheckableAssemblyReference> assemblyReferences;
		
		List<ServiceItem> items = new List<ServiceItem>();
		ServiceItem myItem;
		
		Uri discoveryUri;
		CredentialCache credentialCache = new CredentialCache();
		WebServiceDiscoveryClientProtocol discoveryClientProtocol;
		ServiceReferenceDiscoveryClient serviceReferenceDiscoveryClient;
		
		delegate DiscoveryDocument DiscoverAnyAsync(string url);
		delegate void DiscoveredWebServicesHandler(DiscoveryClientProtocol protocol);
		delegate void AuthenticationHandler(Uri uri, string authenticationType);
		
		public AddServiceReferenceViewModel(IProject project)
		{
			this.project = project;
			this.serviceGenerator = new ServiceReferenceGenerator(project);
			this.assemblyReferences = serviceGenerator.GetCheckableAssemblyReferences().ToList();
			HeadLine = header;
			
			GoCommand = new RelayCommand(ExecuteGo, CanExecuteGo);
			AdvancedDialogCommand = new RelayCommand(ExecuteAdvancedDialogCommand, CanExecuteAdvancedDialogCommand);
			TwoValues = new ObservableCollection<ImageAndDescription>();
		}
		
		#region Go Command
		
		public ICommand GoCommand { get; private set; }
		
		void ExecuteGo()
		{
			if (String.IsNullOrEmpty(SelectedService)) {
				MessageBox.Show(noUrl);
				return;
			}
			ServiceDescriptionMessage = waitMessage;
			Uri uri = new Uri(SelectedService);
			StartDiscovery(uri, new DiscoveryNetworkCredential(CredentialCache.DefaultNetworkCredentials, DiscoveryNetworkCredential.DefaultAuthenticationType));
		}
		
		bool CanExecuteGo()
		{
			return true;
		}
		
		#endregion
		
		#region AdvancedDialogCommand
		
		public ICommand AdvancedDialogCommand { get; private set; }
		
		bool CanExecuteAdvancedDialogCommand()
		{
			return true;
		}
		
		void ExecuteAdvancedDialogCommand()
		{
			var vm = new AdvancedServiceViewModel(serviceGenerator.Options.Clone());
			vm.AssembliesToReference.AddRange(assemblyReferences);
			var view = new AdvancedServiceDialog();
			view.DataContext = vm;
			if (view.ShowDialog() ?? false) {
				serviceGenerator.Options = vm.Options;
				serviceGenerator.UpdateAssemblyReferences(assemblyReferences);
			}
		}
			
		#endregion
		
		#region discover service Code from Matt

		void StartDiscovery(Uri uri, DiscoveryNetworkCredential credential)
		{
			// Abort previous discovery.
			StopDiscovery();
			
			// Start new discovery.
			discoveryUri = uri;
			DiscoverAnyAsync asyncDelegate = new DiscoverAnyAsync(discoveryClientProtocol.DiscoverAny);
			AsyncCallback callback = new AsyncCallback(DiscoveryCompleted);
			discoveryClientProtocol.Credentials = credential;
			IAsyncResult result = asyncDelegate.BeginInvoke(uri.AbsoluteUri, callback, new AsyncDiscoveryState(discoveryClientProtocol, uri, credential));
			
			serviceReferenceDiscoveryClient.DiscoveryComplete += ServiceReferenceDiscoveryComplete;
			serviceReferenceDiscoveryClient.Discover(uri);
		}

		void ServiceReferenceDiscoveryComplete(object sender, ServiceReferenceDiscoveryEventArgs e)
		{
			if (Object.ReferenceEquals(serviceReferenceDiscoveryClient, sender)) {
				if (e.HasError) {
					OnWebServiceDiscoveryError(e.Error);
				} else {
					DiscoveredWebServices(e.Services);
				}
			}
		}
		
		void OnWebServiceDiscoveryError(Exception ex)
		{
			ServiceDescriptionMessage = ex.Message;
			ICSharpCode.Core.LoggingService.Debug("DiscoveryCompleted: " + ex.ToString());
		}
		
		/// <summary>
		/// Called after an asynchronous web services search has
		/// completed.
		/// </summary>
		void DiscoveryCompleted(IAsyncResult result)
		{
			AsyncDiscoveryState state = (AsyncDiscoveryState)result.AsyncState;
			WebServiceDiscoveryClientProtocol protocol = state.Protocol;
			
			// Check that we are still waiting for this particular callback.
			bool wanted = false;
			lock (this) {
				wanted = Object.ReferenceEquals(discoveryClientProtocol, protocol);
			}
			
			if (wanted) {
				DiscoveredWebServicesHandler handler = new DiscoveredWebServicesHandler(DiscoveredWebServices);
				try {
					DiscoverAnyAsync asyncDelegate = (DiscoverAnyAsync)((AsyncResult)result).AsyncDelegate;
					DiscoveryDocument handlerdoc = asyncDelegate.EndInvoke(result);
					handler(protocol);
				} catch (Exception ex) {
					OnWebServiceDiscoveryError(ex);
				}
			}
		}
		
		/// <summary>
		/// Stops any outstanding asynchronous discovery requests.
		/// </summary>
		void StopDiscovery()
		{
			lock (this) {
				if (discoveryClientProtocol != null) {
					try {
						discoveryClientProtocol.Abort();
					} catch (NotImplementedException) {
					} catch (ObjectDisposedException) {
						// Receive this error if the url pointed to a file.
						// The discovery client will already have closed the file
						// so the abort fails.
					}
					discoveryClientProtocol.Dispose();
				}
				discoveryClientProtocol = new WebServiceDiscoveryClientProtocol();
				serviceReferenceDiscoveryClient = new ServiceReferenceDiscoveryClient();
			}
		}
		
		void DiscoveredWebServices(DiscoveryClientProtocol protocol)
		{
			if (protocol != null) {
				ServiceDescriptionCollection services = ServiceReferenceHelper.GetServiceDescriptions(protocol);
				DiscoveredWebServices(services);
			}
		}
		
		void DiscoveredWebServices(ServiceDescriptionCollection services)
		{
			ServiceDescriptionMessage = String.Format(
				"{0} service(s) found at address {1}",
			    services.Count,
			    discoveryUri);
			if (services.Count > 0) {
				AddUrlToHistory(discoveryUri);
			}
			DefaultNameSpace = GetDefaultNamespace();
			FillItems(services);
			string referenceName = ServiceReferenceHelper.GetReferenceName(discoveryUri);
		}
		
		void AddUrlToHistory(Uri discoveryUri)
		{
			urlHistory.AddUrl(discoveryUri);
			RaisePropertyChanged("MruServices");
		}
		
		/// <summary>
		/// Gets the namespace to be used with the generated web reference code.
		/// </summary>
		string GetDefaultNamespace()
		{
			if (namespacePrefix.Length > 0 && discoveryUri != null) {
				return String.Concat(namespacePrefix, ".", discoveryUri.Host);
			} else if (discoveryUri != null) {
				return discoveryUri.Host;
			}
			return String.Empty;
		}
		
		#endregion
		
		public string Title
		{
			get { return title; }
			set {
				title = value;
				base.RaisePropertyChanged(() => Title);
			}
		}
		
		public string HeadLine { get; set; }
		
		public List<string> MruServices {
			get { return urlHistory.Urls; }
		}
		
		public string SelectedService {
			get { return selectedService; }
			set {
				selectedService = value;
				base.RaisePropertyChanged(() => SelectedService);
			}
		}
	
		public List <ServiceItem> ServiceItems {
			get { return items; }
			set {
				items = value;
				base.RaisePropertyChanged(() => ServiceItems);
			}
		}
		
		public ServiceItem ServiceItem {
			get { return myItem; }
			set {
				myItem = value;
				UpdateListView();
				base.RaisePropertyChanged(() => ServiceItem);
			}
		}
		
		public string ServiceDescriptionMessage {
			get { return serviceDescriptionMessage; }
			set {
				serviceDescriptionMessage = value;
				base.RaisePropertyChanged(() => ServiceDescriptionMessage);
			}
		}
		
		public string DefaultNameSpace {
			get { return defaultNameSpace; }
			set {
				defaultNameSpace = value;
				base.RaisePropertyChanged(() => DefaultNameSpace);
			}
		}
		
		public ObservableCollection<ImageAndDescription> TwoValues {
			get { return twoValues; }
			set {
				twoValues = value;
				base.RaisePropertyChanged(() => TwoValues);
			}
		}
		
		//http://mikehadlow.blogspot.com/2006/06/simple-wsdl-object.html
		
		void UpdateListView()
		{
			TwoValues.Clear();
			if (ServiceItem.Tag is ServiceDescription) {
				ServiceDescription desc = (ServiceDescription)ServiceItem.Tag;
				var tv = new ImageAndDescription(PresentationResourceService.GetBitmapSource("Icons.16x16.Interface"),
				                                 desc.RetrievalUrl);
				TwoValues.Add(tv);
			} else if (ServiceItem.Tag is PortType) {
				PortType portType = (PortType)ServiceItem.Tag;
				foreach (Operation op in portType.Operations) {
					TwoValues.Add(new ImageAndDescription(PresentationResourceService.GetBitmapSource("Icons.16x16.Method"),
					                                      op.Name));
				}
			}
		}
		
		void FillItems(ServiceDescriptionCollection descriptions)
		{
			foreach (ServiceDescription element in descriptions) {
				Add(element);
			}
		}
		
		void Add(ServiceDescription description)
		{
			List<ServiceItem> items = new List<ServiceItem>();
			var name = ServiceReferenceHelper.GetServiceName(description);
			var rootNode = new ServiceItem(null, name);
			rootNode.Tag = description;

			foreach (Service service in description.Services) {
				var serviceNode = new ServiceItem(null, service.Name);
				serviceNode.Tag = service;
				items.Add(serviceNode);
				foreach (PortType portType  in description.PortTypes) {
					var portNode = new ServiceItem(PresentationResourceService.GetBitmapSource("Icons.16x16.Interface"), portType.Name);
					portNode.Tag = portType;
					serviceNode.SubItems.Add(portNode);
				}
			}
			ServiceItems = items;
		}
		
		public void AddServiceReference()
		{
			CompilerMessageView.Instance.BringToFront();
			serviceGenerator.Options.Namespace = defaultNameSpace;
			serviceGenerator.Options.Url = discoveryUri.ToString();
			serviceGenerator.AddServiceReference();
			new RefreshProjectBrowser().Run();
		}
	}
	
	public class ImageAndDescription
	{
		public ImageAndDescription(BitmapSource bitmapSource, string description)
		{
			Image = bitmapSource;
			Description = description;
		}
		
		public BitmapSource Image { get; set; }
		public string Description { get; set; }
	}
	
	public class ServiceItem : ImageAndDescription
	{
		public ServiceItem(BitmapSource bitmapSource, string description) : base(bitmapSource, description)
		{
			SubItems = new List<ServiceItem>();
		}
		public object Tag { get; set; }
		public List<ServiceItem> SubItems { get; set; }
	}
	
	public class CheckableAssemblyReference : ImageAndDescription
	{
		static BitmapSource ReferenceImage;

		ReferenceProjectItem projectItem;
		
		public CheckableAssemblyReference(ReferenceProjectItem projectItem)
			: this(projectItem.AssemblyName.ShortName)
		{
			this.projectItem = projectItem;
		}
		
		protected CheckableAssemblyReference(string description)
			: base(GetReferenceImage(), description)
		{
		}
		
		static BitmapSource GetReferenceImage()
		{
			try {
				if (ReferenceImage == null) {
					ReferenceImage = PresentationResourceService.GetBitmapSource("Icons.16x16.Reference");
				}
				return ReferenceImage;
			} catch (Exception) {
				return null;
			}
		}
		
		public bool ItemChecked { get; set; }
		
		public string GetFileName()
		{
			if (projectItem != null) {
				return projectItem.FileName;
			}
			return Description;
		}
	}
}
