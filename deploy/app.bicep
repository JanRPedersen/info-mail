param location string = resourceGroup().location
param identityId string
param identityClientId string
param registryServer string
param environmentId string
param imageTag string

resource app 'Microsoft.App/containerApps@2024-03-01' = {
  name: 'infomail-pilot'
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${identityId}': {} }
  }
  properties: {
    managedEnvironmentId: environmentId
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      registries: [
        { server: registryServer, identity: identityId }
      ]
    }
    template: {
      containers: [
        {
          name: 'infomail'
          image: '${registryServer}/infomail:${imageTag}'
          resources: { cpu: json('0.25'), memory: '0.5Gi' }
          env: [
            { name: 'AzurePilot__ManagedIdentityClientId', value: identityClientId }
            { name: 'AzurePilot__ReadingEnabled', value: 'false' }
            { name: 'AiShadow__Enabled', value: 'false' }
            { name: 'AiShadow__DataProcessingApproved', value: 'false' }
            { name: 'Processing__Enabled', value: 'false' }
            { name: 'Processing__SendEnabled', value: 'false' }
            { name: 'Processing__MoveEnabled', value: 'false' }
            { name: 'DOTNET_ENVIRONMENT', value: 'Production' }
          ]
        }
      ]
      scale: { minReplicas: 1, maxReplicas: 1 }
    }
  }
}

output appId string = app.id
