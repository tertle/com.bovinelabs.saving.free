# BovineLabs Savanna - DOTS Saving - Free
The free edition of BovineLabs Savanna Saving for Dots

Support: https://discord.gg/RTsw6Cxvw3

# Installation
Savanna is a saving system for Unity’s Entities package. If you do not use Entities in your project then this package is not for you.

The minimum requirements for the package is
- Entities: 1.3.2 and newer
- BovineLabs Core: 1.3.4 and newer (available https://gitlab.com/tertle/com.bovinelabs.core or https://openupm.com/packages/com.bovinelabs.core/)

The samples also require
- Entities Graphics
- Universal RP

# Quick Start
## Save Attribute
To make a IComponentData or IBufferElement savable all that is required is to add the `Save` attribute to the to component. This can be applied to `IComponentData`, `IBufferElement` and and tag components that have `IEnableableComponent`. 

```cs
[Save]
public struct TestComponentData : IComponentData
{    
    public int Value;
}
```

```cs
[Save]
public struct TestBufferElement : IBufferElement
{    
    public float Value;
}
```

```cs
[Save]
public struct TestTag : IComponentData, IEnableableComponent
{    
}
```

> [!WARNING]
> Using `[Save]` on an zero sized component that isn't marked as enableable will result in an error. Savanna requires static archetypes therefore will not add or remove components these tag components, instead only save the enableable state. Support for adding and removing tag components is under consideration.

Saving managed components is not supported and it's advised to avoid needing to do this however if this is something you need it is possible by adding your own custom saving into the pipeline.

If you don’t have access to the assembly that has a component you want to serialize, for example a component inside a Unity package, it is still possible to save the component by adding it via the save builder.

> [!NOTE]
> By default LocalTransform and PhysicsVelocity (if Unity Physics exists) are saved. Static transforms with only LocalToWorld should not need serializing therefore it is not included by default.

## Ignoring Fields
If you don’t want to save an entire component you can use the `SaveIgnoreAttribute` to fields and they will be ignored when deserializing.

``` cs
[Save]
public struct TestComponentData : IComponentData
{    
    public int Value0;
    public int Value1;
    
    [SaveIgnore] 
    public byte Value2;
}
```

This attribute will still cause the entire component to be serialized and will not reduce file size; instead when deserialized the data will simply be ignored. This is done for performance and also to allow you to change your mind. If you wanted the field to no longer be ignored you could safely remove the attribute and existing saves will have the data and deserialize correctly.

It is more performant to group all fields that have this attribute on them together at either the top or bottom of the struct. This will minimize the number of MemCpyReplicates that need to be executed on deserializing. Each group of serializable fields requires a separate copy so only a single call is required when they are all grouped together. 

## IEnableableComponent
If a `IComponentData` or `IBufferElement` is marked to be saved and inherits from the `IEnableableComponent`, then the enabled state of the component will also be saved along side the component and applied back on loading.
