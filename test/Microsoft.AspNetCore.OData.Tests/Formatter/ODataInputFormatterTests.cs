// Copyright (c) Microsoft Corporation.  All rights reserved.
// Licensed under the MIT License.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.OData.Extensions;
using Microsoft.AspNetCore.OData.Formatter;
using Microsoft.AspNetCore.OData.Formatter.Value;
using Microsoft.AspNetCore.OData.Tests.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OData;
using Microsoft.OData.Edm;
using Microsoft.OData.ModelBuilder;
using Microsoft.OData.UriParser;
using Moq;
using Xunit;

namespace Microsoft.AspNetCore.OData.Tests.Formatter
{
    public class ODataInputFormatterTests
    {
        private static IEdmModel _edmModel = GetEdmModel();

        [Fact]
        public void ConstructorThrowsIfPayloadsIsNull()
        {
            // Arrange & Act & Assert
            Assert.Throws<ArgumentNullException>("payloadKinds", () => new ODataInputFormatter(payloadKinds: null));
        }

        [Fact]
        public void CanReadThrowsIfContextIsNull()
        {
            // Arrange & Act
            ODataInputFormatter formatter = new ODataInputFormatter(new[] { ODataPayloadKind.Resource });

            // Assert
            Assert.Throws<ArgumentNullException>("context", () => formatter.CanRead(context: null));
        }

        [Fact]
        public void CanReadReturnsFalseIfNoODataPathSet()
        {
            // Arrange & Act
            InputFormatterContext context = new InputFormatterContext(
                new DefaultHttpContext(),
                "modelName",
                new ModelStateDictionary(),
                new EmptyModelMetadataProvider().GetMetadataForType(typeof(int)),
                (stream, encoding) => new StreamReader(stream, encoding));

            ODataInputFormatter formatter = new ODataInputFormatter(new[] { ODataPayloadKind.Resource });

            // Assert
            Assert.False(formatter.CanRead(context));
        }

        [Theory]
        [InlineData(typeof(Customer), ODataPayloadKind.Resource, true)]
        [InlineData(typeof(Customer), ODataPayloadKind.Property, false)]
        [InlineData(typeof(Customer[]), ODataPayloadKind.ResourceSet, true)]
        [InlineData(typeof(int), ODataPayloadKind.Property, true)]
        [InlineData(typeof(IList<double>), ODataPayloadKind.Collection, true)]
        [InlineData(typeof(ODataActionParameters), ODataPayloadKind.Parameter, true)]
        [InlineData(typeof(Delta<Customer>), ODataPayloadKind.Resource, true)]
        [InlineData(typeof(IEdmEntityObject), ODataPayloadKind.Resource, true)]
        public void CanReadReturnsBooleanValueAsExpected(Type type, ODataPayloadKind payloadKind, bool expected)
        {
            // Arrange & Act
            IEdmEntitySet entitySet = _edmModel.EntityContainer.FindEntitySet("Customers");
            EntitySetSegment entitySetSeg = new EntitySetSegment(entitySet);
            HttpRequest request = RequestFactory.Create(opt => opt.AddModel("odata", _edmModel));
            request.ODataFeature().PrefixName = "odata";
            request.ODataFeature().Model = _edmModel;
            request.ODataFeature().Path = new ODataPath(entitySetSeg);

            InputFormatterContext context = CreateInputContext(type, request);

            ODataInputFormatter formatter = new ODataInputFormatter(new[] { payloadKind });

            // Assert
            Assert.Equal(expected, formatter.CanRead(context));
        }

        [Fact]
        public Task ReadFromStreamAsyncDoesNotCloseStreamWhenContentLengthIsZero()
        {
            // Arrange
            ODataInputFormatter formatter = GetInputFormatter();
            Mock<Stream> mockStream = new Mock<Stream>();

            byte[] contentBytes = Encoding.UTF8.GetBytes(string.Empty);
            HttpContext httpContext = GetHttpContext(contentBytes);
            InputFormatterContext formatterContext = CreateInputFormatterContext(typeof(Customer), httpContext);

            // Act
            return formatter.ReadRequestBodyAsync(formatterContext, Encoding.UTF8)
                .ContinueWith(
                    readTask =>
                    {
                        // Assert
                        Assert.Equal(TaskStatus.RanToCompletion, readTask.Status);
                        mockStream.Verify(s => s.Close(), Times.Never());
                    });
        }

        [Theory]
        [InlineData(false)]
        [InlineData(0)]
        [InlineData("")]
        public async Task ReadRequestBodyAsyncReturnsDefaultTypeValueWhenContentLengthIsZero<T>(T value)
        {
            // Arrange
            ODataInputFormatter formatter = GetInputFormatter();
            byte[] contentBytes = Encoding.UTF8.GetBytes("");
            HttpContext httpContext = GetHttpContext(contentBytes);

            InputFormatterContext formatterContext = CreateInputFormatterContext(typeof(T), httpContext);

            // Act
            InputFormatterResult result = await formatter.ReadRequestBodyAsync(formatterContext, Encoding.UTF8);

            // Assert
            Assert.False(result.HasError);
            Type valueType = value.GetType();
            if (valueType.IsValueType)
            {
                T actualResult = Assert.IsType<T>(result.Model);
                Assert.Equal(default(T), actualResult);
            }
            else
            {
                Assert.Null(result.Model);
            }
        }

        [Fact]
        public Task ReadRequestBodyAsyncReadsDataButDoesNotCloseStreamWhenContentLengthIsNull()
        {
            // Arrange
            byte[] expectedSampleTypeByte = Encoding.UTF8.GetBytes(
                "{" +
                    "\"@odata.context\":\"http://localhost/$metadata#Customers/$entity\"," +
                    "\"Number\":42" +
                 "}");

            ODataInputFormatter formatter = GetInputFormatter();

            HttpContext httpContext = GetHttpContext(expectedSampleTypeByte);
            httpContext.Request.ContentType = "application/json;odata.metadata=minimal";
            httpContext.Request.ContentLength = null;
            Stream memStream = httpContext.Request.Body;

            InputFormatterContext formatterContext = CreateInputFormatterContext(typeof(Customer), httpContext);

            // Act
            return formatter.ReadRequestBodyAsync(formatterContext, Encoding.UTF8).ContinueWith(
                readTask =>
                {
                    // Assert
                    Assert.Equal(TaskStatus.RanToCompletion, readTask.Status);
                    Assert.True(memStream.CanRead);

                    InputFormatterResult result = Assert.IsType<InputFormatterResult>(readTask.Result);
                    Assert.Null(result.Model);
                });
        }

        [Fact]
        public Task ReadRequestBodyAsyncReadsDataButDoesNotCloseStreamWhenContentLengthl()
        {
            // Arrange
            byte[] expectedSampleTypeByte = Encoding.UTF8.GetBytes(
                "{" +
                    "\"@odata.context\":\"http://localhost/$metadata#Customers/$entity\"," +
                    "\"Number\":42" +
                "}");

            IEdmSingleton singleton = _edmModel.EntityContainer.FindSingleton("Me");
            SingletonSegment singletonSeg = new SingletonSegment(singleton);

            ODataInputFormatter formatter = GetInputFormatter();
            formatter.BaseAddressFactory = (request) => new Uri("http://localhost");

            HttpContext httpContext = GetHttpContext(expectedSampleTypeByte, opt => opt.AddModel("odata", _edmModel));
            httpContext.Request.ContentType = "application/json;odata.metadata=minimal";
            httpContext.Request.ContentLength = expectedSampleTypeByte.Length;
            httpContext.ODataFeature().Model = _edmModel;
            httpContext.ODataFeature().PrefixName = "odata";
            httpContext.ODataFeature().Path = new ODataPath(singletonSeg);
            Stream memStream = httpContext.Request.Body;

            InputFormatterContext formatterContext = CreateInputFormatterContext(typeof(Customer), httpContext);

            // Act
            return formatter.ReadRequestBodyAsync(formatterContext, Encoding.UTF8).ContinueWith(
                readTask =>
                {
                    // Assert
                    Assert.Equal(TaskStatus.RanToCompletion, readTask.Status);
                    Assert.True(memStream.CanRead);

                    var value = Assert.IsType<Customer>(readTask.Result.Model);
                    Assert.Equal(42, value.Number);
                });
        }

        private static ODataInputFormatter GetInputFormatter()
        {
            return ODataInputFormatterFactory.Create().FirstOrDefault();
        }

        protected static HttpContext GetHttpContext(byte[] contentBytes, Action<ODataOptions> setupAction = null)
        {
            MemoryStream stream = new MemoryStream(contentBytes);
            DefaultHttpContext httpContext = new DefaultHttpContext();
            httpContext.Request.Body = stream;
            httpContext.Request.ContentType = "application/json";

            IServiceCollection services = new ServiceCollection();
            if (setupAction != null)
            {
                services.Configure(setupAction);
            }

            httpContext.RequestServices = services.BuildServiceProvider();
            return httpContext;
        }

        protected static InputFormatterContext CreateInputFormatterContext(
            Type modelType,
            HttpContext httpContext,
            string modelName = null)
        {
            var provider = new EmptyModelMetadataProvider();
            var metadata = provider.GetMetadataForType(modelType);

            return new InputFormatterContext(
                httpContext,
                modelName: modelName ?? string.Empty,
                modelState: new ModelStateDictionary(),
                metadata: metadata,
                readerFactory: (stream, encoding) => new StreamReader(stream, encoding));
        }

        private static InputFormatterContext CreateInputContext(Type type, HttpRequest request)
        {
            return new InputFormatterContext(
                request.HttpContext,
                "modelName",
                new ModelStateDictionary(),
                new EmptyModelMetadataProvider().GetMetadataForType(type),
                (stream, encoding) => new StreamReader(stream, encoding));
        }

        private static IEdmModel GetEdmModel()
        {
            ODataConventionModelBuilder model = new ODataConventionModelBuilder();
            model.EntitySet<Customer>("Customers");
            model.Singleton<Customer>("Me");
            model.ComplexType<Address>();
            return model.GetEdmModel();
        }

        private class Customer
        {
            public int Id { get; set; }

            public int Number { get; set; }
        }

        private class Address
        {
            public string Street { get; set; }
        }
    }
}