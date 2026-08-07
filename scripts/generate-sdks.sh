#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
contract_directory="${repository_root}/openapi"
sdk_root="${repository_root}/sdk"
generator_image="openapitools/openapi-generator-cli:v7.14.0@sha256:a620610d9fabf7ce05310c648417ba168125aac2f4517580030e115921ac1a52"
temporary="$(mktemp -d "${TMPDIR:-/tmp}/lakehold-sdks.XXXXXX")"
trap 'rm -rf "${temporary}"' EXIT

generate() {
    local language="$1"
    local generator="$2"
    local git_repository="$3"
    local properties="$4"
    mkdir -p "${temporary}/${language}"
    docker run --rm \
        --user "$(id -u):$(id -g)" \
        --env HOME=/tmp \
        --volume "${contract_directory}:/contract:ro" \
        --volume "${temporary}/${language}:/output" \
        "${generator_image}" generate \
        --input-spec /contract/lakehold-v1.json \
        --generator-name "${generator}" \
        --output /output \
        --git-user-id skuirrels \
        --git-repo-id "${git_repository}" \
        --global-property apiDocs=false,modelDocs=false,apiTests=false,modelTests=false \
        --additional-properties "${properties}"
}

generate java java LakeHold 'groupId=io.lakehold,artifactId=lakehold-sdk,artifactVersion=0.1.0,artifactUrl=https://github.com/skuirrels/LakeHold,artifactDescription=Official client for the LakeHold v1 public API,invokerPackage=io.lakehold.sdk,apiPackage=io.lakehold.sdk.api,modelPackage=io.lakehold.sdk.model,library=okhttp-gson,dateLibrary=java8,useJakartaEe=true,hideGenerationTimestamp=true,useSingleRequestParameter=true,disallowAdditionalPropertiesIfNotPresent=false,licenseName=Apache License 2.0,licenseUrl=https://www.apache.org/licenses/LICENSE-2.0,developerName=LakeHold contributors,developerEmail=,developerOrganization=LakeHold,developerOrganizationUrl=https://github.com/skuirrels/LakeHold,scmConnection=scm:git:https://github.com/skuirrels/LakeHold.git,scmDeveloperConnection=scm:git:ssh://git@github.com/skuirrels/LakeHold.git,scmUrl=https://github.com/skuirrels/LakeHold'
generate go go LakeHold/sdk/go 'packageName=lakehold,packageVersion=0.1.0,withGoMod=true,enumClassPrefix=true,hideGenerationTimestamp=true,disallowAdditionalPropertiesIfNotPresent=false,licenseId=Apache-2.0'
generate dotnet csharp LakeHold 'packageName=Lakehold.Sdk,packageVersion=0.1.0,packageGuid={44679FE8-7E07-42E2-B915-3EA960021C4D},targetFramework=net8.0,nullableReferenceTypes=true,library=generichost,hideGenerationTimestamp=true,licenseId=Apache-2.0,packageAuthors=LakeHold contributors,packageCompany=LakeHold,packageCopyright=Copyright LakeHold contributors,packageDescription=Official client for the LakeHold v1 public API,packageTags=lakehold;lakehouse;data-platform'
generate python python LakeHold 'packageName=lakehold_sdk,projectName=lakehold-sdk,packageVersion=0.1.0,packageUrl=https://github.com/skuirrels/LakeHold,packageAuthor=LakeHold contributors,packageAuthorEmail=,library=urllib3,hideGenerationTimestamp=true,licenseId=Apache-2.0'

# A few templates hard-code generator-community package metadata and ignore their documented
# additional properties. Normalize those exact manifest fields without touching generated runtime
# source; this prevents placeholder ownership or licensing from entering a release artifact.
perl -0pi -e 's/authors = \[\n  \{name = "OpenAPI Generator Community",email = "team\@openapitools\.org"\},\n\]/authors = [\n  {name = "LakeHold contributors"},\n]/' "${temporary}/python/pyproject.toml"
perl -pi -e 's/^license = "NoLicense"$/license = "Apache-2.0"/' "${temporary}/python/pyproject.toml"
perl -pi -e 's/^name = "lakehold_sdk"$/name = "lakehold-sdk"/' "${temporary}/python/pyproject.toml"
perl -pi -e 's/author="OpenAPI Generator community"/author="LakeHold contributors"/; s/author_email="team\@openapitools\.org"/author_email=""/' "${temporary}/python/setup.py"
perl -0pi -e 's/PYTHON_REQUIRES = ">= 3\.9"\nREQUIRES = \[.*?\]\n\n//s; s/    install_requires=REQUIRES,\n//' "${temporary}/python/setup.py"
find "${temporary}/python/lakehold_sdk" -type f -name '*.py' \
    -exec perl -ni -e 'print unless /# TODO/' {} \;
perl -ni -e 'print unless /# TODO/' "${temporary}/python/pyproject.toml"
# The C# generator documents implemented JSON converter methods as throwing
# NotImplementedException. Remove that false contract claim from every generated model.
find "${temporary}/dotnet/src/Lakehold.Sdk/Model" -type f -name '*.cs' \
    -exec perl -ni -e 'print unless /<exception cref="NotImplementedException"><\/exception>/' {} \;
# The generator's OrDefault helpers catch every exception, including caller cancellation. Preserve
# their documented null-on-failure convenience without turning cancellation into a false null result.
find "${temporary}/dotnet/src/Lakehold.Sdk/Api" -type f -name '*.cs' \
    -exec perl -0pi -e 's/(            )catch \(Exception\)\n\1\{\n\1    return null;\n\1\}/${1}catch (OperationCanceledException)\n${1}{\n${1}    throw;\n${1}}\n${1}catch (Exception)\n${1}{\n${1}    return null;\n${1}}/g' {} \;
perl -pi -e 's/<AssemblyTitle>OpenAPI Library<\/AssemblyTitle>/<AssemblyTitle>LakeHold SDK<\/AssemblyTitle>/; s/<PackageReleaseNotes>Minor update<\/PackageReleaseNotes>/<PackageReleaseNotes>Initial v1 public API client.<\/PackageReleaseNotes>/' "${temporary}/dotnet/src/Lakehold.Sdk/Lakehold.Sdk.csproj"
perl -0pi -e 's#    <PackageLicenseExpression>Apache-2.0</PackageLicenseExpression>#    <PackageLicenseExpression>Apache-2.0</PackageLicenseExpression>\n    <PackageReadmeFile>README.md</PackageReadmeFile>#' "${temporary}/dotnet/src/Lakehold.Sdk/Lakehold.Sdk.csproj"
perl -0pi -e 's#  <ItemGroup>\n    <PackageReference#  <ItemGroup>\n    <None Update="README.md" Pack="true" PackagePath="\\" />\n  </ItemGroup>\n\n  <ItemGroup>\n    <PackageReference#' "${temporary}/dotnet/src/Lakehold.Sdk/Lakehold.Sdk.csproj"
perl -0pi -e 's#    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>#    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>\n    <!-- Generated public symbols retain contract names; suppress only conflicting design-style rules. -->\n    <NoWarn>\$(NoWarn);CA1507;CA1510;CA1707;CA1711;CA1715</NoWarn>#' "${temporary}/dotnet/src/Lakehold.Sdk/Lakehold.Sdk.csproj"
perl -pi -e 's/^    <description>OpenAPI Java<\/description>$/    <description>Official client for the LakeHold v1 public API<\/description>/; $_ = "" if /^\s*<email><\/email>\s*$/' "${temporary}/java/pom.xml"
perl -pi -e 's/using Org\.OpenAPITools\.Extensions;/using Lakehold.Sdk.Extensions;/; s/GetApiCapabilitiesAsync\("todo"\)/GetApiCapabilitiesAsync()/' "${temporary}/dotnet/src/Lakehold.Sdk/README.md"
perl -0pi -e 's/If the python package is hosted on a repository, you can install directly using:\n\n```sh\npip install git\+https:\/\/github\.com\/skuirrels\/LakeHold\.git\n```\n\(you may need to run `pip` with root permission: `sudo pip install git\+https:\/\/github\.com\/skuirrels\/LakeHold\.git`\)/The package is not published yet. From the `sdk\/python` directory, install it into an isolated virtual environment:\n\n```sh\npython -m venv .venv\n. .venv\/bin\/activate\npython -m pip install .\n```/' "${temporary}/python/README.md"
perl -0pi -e 's/### Setuptools\n\nInstall via \[Setuptools\]\(http:\/\/pypi\.python\.org\/pypi\/setuptools\)\.\n\n```sh\npython setup\.py install --user\n```\n\(or `sudo python setup\.py install` to install the package for all users\)\n\nThen import the package:\n```python\nimport lakehold_sdk\n```\n\n//' "${temporary}/python/README.md"

# Generator debug modes and default model renderers are not secret-safe. Keep wire/body logging
# disabled, redact bearer authentication from Java's header diagnostics, and ensure accidental
# string formatting cannot expose the one-time token returned by token creation. Explicit JSON
# serialization remains unchanged because callers need the token value to persist it securely.
perl -0pi -e 's/loggingInterceptor = new HttpLoggingInterceptor\(\);\n                loggingInterceptor\.setLevel\(Level\.BODY\);/loggingInterceptor = new HttpLoggingInterceptor();\n                loggingInterceptor.redactHeader("Authorization");\n                loggingInterceptor.setLevel(Level.HEADERS);/' "${temporary}/java/src/main/java/io/lakehold/sdk/ApiClient.java"
perl -pi -e 's/sb\.append\("    token: "\)\.append\(toIndentedString\(token\)\)\.append\("\\n"\);/sb.append("    token: <redacted>\\n");/' "${temporary}/java/src/main/java/io/lakehold/sdk/model/CreatedTokenDto.java"
perl -pi -e 's/sb\.Append\("  Token: "\)\.Append\(Token\)\.Append\("\\n"\);/sb.Append("  Token: <redacted>\\n");/' "${temporary}/dotnet/src/Lakehold.Sdk/Model/CreatedTokenDto.cs"
perl -0pi -e 's/from pydantic import BaseModel, ConfigDict, StrictInt, StrictStr/from pydantic import BaseModel, ConfigDict, Field, StrictInt, StrictStr/; s/token: StrictStr\n/token: StrictStr = Field(repr=False)\n/; s/return pprint\.pformat\(self\.model_dump\(by_alias=True\)\)/values = self.model_dump(by_alias=True)\n        values["token"] = "<redacted>"\n        return pprint.pformat(values)/' "${temporary}/python/lakehold_sdk/models/created_token_dto.py"
perl -pi -e 's/httplib\.HTTPConnection\.debuglevel = 1/httplib.HTTPConnection.debuglevel = 0  # Wire dumps can expose credentials and one-time secrets./' "${temporary}/python/lakehold_sdk/configuration.py"
perl -pi -e 's/# turn on httplib debug/# keep raw httplib wire dumps disabled/' "${temporary}/python/lakehold_sdk/configuration.py"
perl -0pi -e 's#func \(o CreatedTokenDto\) MarshalJSON#// String returns a diagnostic-safe representation. Use MarshalJSON when explicit serialization is required.\nfunc (o CreatedTokenDto) String() string {\n\treturn fmt.Sprintf("CreatedTokenDto{Id:%d Name:%q Token:<redacted>}", o.Id, o.Name)\n}\n\nfunc (o CreatedTokenDto) MarshalJSON#' "${temporary}/go/model_created_token_dto.go"
perl -0pi -e 's#if c\.cfg\.Debug \{\n\t\tdump, err := httputil\.DumpRequestOut\(request, true\)\n\t\tif err != nil \{\n\t\t\treturn nil, err\n\t\t\}\n\t\tlog\.Printf\("\\n%s\\n", string\(dump\)\)\n\t\}#if c.cfg.Debug {\n\t\tlog.Printf("LakeHold request %s %s", request.Method, request.URL.EscapedPath())\n\t}#; s#if c\.cfg\.Debug \{\n\t\tdump, err := httputil\.DumpResponse\(resp, true\)\n\t\tif err != nil \{\n\t\t\treturn resp, err\n\t\t\}\n\t\tlog\.Printf\("\\n%s\\n", string\(dump\)\)\n\t\}#if c.cfg.Debug {\n\t\tlog.Printf("LakeHold response %s %s", resp.Status, request.URL.EscapedPath())\n\t}#; s#\n\t"net/http/httputil"##' "${temporary}/go/client.go"

# The API templates emit trailing spaces in generated operation documentation. Normalize only the
# three affected aggregate API files, rather than creating formatting churn in model files.
perl -pi -e 's/[ \t]+$//' \
    "${temporary}/java/src/main/java/io/lakehold/sdk/api/LakehouseApi.java" \
    "${temporary}/dotnet/src/Lakehold.Sdk/Api/LakehouseApi.cs" \
    "${temporary}/python/lakehold_sdk/api/lakehouse_api.py"

# Generator templates disagree on how many blank lines belong at end of file. Normalize the
# aggregate files that are rewritten when an operation or schema changes so regeneration stays
# whitespace-clean and reproducible across all four clients.
perl -0pi -e 's/\n+\z/\n/' \
    "${temporary}/java/README.md" \
    "${temporary}/java/api/openapi.yaml" \
    "${temporary}/go/README.md" \
    "${temporary}/go/api/openapi.yaml" \
    "${temporary}/dotnet/README.md" \
    "${temporary}/dotnet/api/openapi.yaml" \
    "${temporary}/python/README.md" \
    "${temporary}/python/lakehold_sdk/api/lakehouse_api.py"

# Treat an upstream template change as a failed generation, not as permission to emit an unsafe
# client. These assertions also make the intended security properties visible in CI output.
grep -Fq 'loggingInterceptor.redactHeader("Authorization");' "${temporary}/java/src/main/java/io/lakehold/sdk/ApiClient.java"
grep -Fq 'loggingInterceptor.setLevel(Level.HEADERS);' "${temporary}/java/src/main/java/io/lakehold/sdk/ApiClient.java"
grep -Fq 'token: <redacted>' "${temporary}/java/src/main/java/io/lakehold/sdk/model/CreatedTokenDto.java"
grep -Fq 'Token: <redacted>' "${temporary}/dotnet/src/Lakehold.Sdk/Model/CreatedTokenDto.cs"
dotnet_default_catches="$(perl -0ne \
    '$count += () = /catch \(Exception\)\s*\{\s*return null;/g; END { print $count // 0 }' \
    "${temporary}/dotnet/src/Lakehold.Sdk/Api/"*.cs)"
dotnet_cancellation_catches="$(perl -0ne \
    '$count += () = /catch \(OperationCanceledException\)/g; END { print $count // 0 }' \
    "${temporary}/dotnet/src/Lakehold.Sdk/Api/"*.cs)"
if [[ "${dotnet_default_catches}" == "0" \
    || "${dotnet_default_catches}" != "${dotnet_cancellation_catches}" ]]; then
    printf 'Generated .NET SDK still swallows caller cancellation in an OrDefault helper.\n' >&2
    exit 1
fi
grep -Fq 'token: StrictStr = Field(repr=False)' "${temporary}/python/lakehold_sdk/models/created_token_dto.py"
grep -Fq 'values["token"] = "<redacted>"' "${temporary}/python/lakehold_sdk/models/created_token_dto.py"
grep -Fq 'CreatedTokenDto{Id:%d Name:%q Token:<redacted>}' "${temporary}/go/model_created_token_dto.go"
grep -Fq 'log.Printf("LakeHold request %s %s", request.Method, request.URL.EscapedPath())' "${temporary}/go/client.go"
if grep -R -n -E 'setLevel\(Level\.BODY\)|HTTPConnection\.debuglevel = 1|DumpRequestOut|DumpResponse' "${temporary}/java/src/main" "${temporary}/go" "${temporary}/python/lakehold_sdk"; then
    printf 'Generated SDK contains secret-unsafe wire or body logging.\n' >&2
    exit 1
fi

# Generator-owned publishing helpers and unrelated CI templates are deliberately excluded. This
# repository owns CI and release policy; generated scripts with placeholder push behavior must not
# be mistaken for an enabled publication path.
rm -rf \
    "${temporary}/java/.github" "${temporary}/java/.openapi-generator" \
    "${temporary}/java/gradle" \
    "${temporary}/dotnet/.github" "${temporary}/dotnet/.openapi-generator" \
    "${temporary}/dotnet/docs" \
    "${temporary}/go/.github" "${temporary}/go/.openapi-generator" \
    "${temporary}/python/.github" "${temporary}/python/.openapi-generator"
rm -f \
    "${temporary}/java/.travis.yml" "${temporary}/java/git_push.sh" \
    "${temporary}/java/build.gradle" "${temporary}/java/build.sbt" \
    "${temporary}/java/gradle.properties" "${temporary}/java/gradlew" \
    "${temporary}/java/gradlew.bat" "${temporary}/java/settings.gradle" \
    "${temporary}/dotnet/appveyor.yml" \
    "${temporary}/go/.travis.yml" "${temporary}/go/git_push.sh" \
    "${temporary}/python/.travis.yml" "${temporary}/python/.gitlab-ci.yml" \
    "${temporary}/python/git_push.sh"

for language in java go dotnet python; do
    target="${sdk_root}/${language}"
    mkdir -p "${target}"
    # Handwritten conformance tests live beside the generated clients and must survive regeneration.
    rsync --archive --delete \
        --exclude 'src/test/' \
        --exclude 'tests/' \
        --exclude 'conformance_test.go' \
        --exclude 'runtime.go' \
        --exclude 'src/main/java/io/lakehold/sdk/runtime/' \
        --exclude 'src/Lakehold.Sdk/Runtime/' \
        --exclude 'lakehold_sdk/runtime/' \
        --exclude 'Lakehold.Sdk.sln' \
        "${temporary}/${language}/" "${target}/"
done

printf 'Generated Java, Go, .NET, and Python SDKs from %s\n' "${contract_directory}/lakehold-v1.json"
