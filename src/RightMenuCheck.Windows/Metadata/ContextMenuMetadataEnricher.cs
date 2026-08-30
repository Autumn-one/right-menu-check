using RightMenuCheck.Core.Inventory;
using RightMenuCheck.Core.Metadata;

namespace RightMenuCheck.Windows.Metadata;

public sealed class ContextMenuMetadataEnricher
{
    private readonly ComServerResolver _comServerResolver;
    private readonly IBinaryMetadataReader _binaryMetadataReader;

    public ContextMenuMetadataEnricher(
        ComServerResolver comServerResolver,
        IBinaryMetadataReader binaryMetadataReader)
    {
        _comServerResolver = comServerResolver ??
                             throw new ArgumentNullException(nameof(comServerResolver));
        _binaryMetadataReader = binaryMetadataReader ??
                                throw new ArgumentNullException(nameof(binaryMetadataReader));
    }

    public ContextMenuRegistrationMetadata Enrich(ContextMenuRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);

        var components = _comServerResolver.Resolve(registration)
            .Select(component =>
            {
                var serverPath = component.ComServer?.ResolvedServerPath;
                return serverPath is null
                    ? component
                    : component with { Binary = _binaryMetadataReader.Read(serverPath) };
            })
            .ToArray();

        return new ContextMenuRegistrationMetadata(
            registration,
            components,
            Owner: null,
            Issues: []);
    }
}
