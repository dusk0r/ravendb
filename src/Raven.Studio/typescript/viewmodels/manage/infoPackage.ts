import viewModelBase = require("viewmodels/viewModelBase");
import endpoints = require("endpoints");
import appUrl = require("common/appUrl");

class infoPackage extends viewModelBase {

    downloadServerWidePackage() {
        this.startDownload(endpoints.global.serverWideDebugInfoPackage.debugInfoPackage);
    }

    downloadClusterWidePackage() {
        this.startDownload(endpoints.global.serverWideDebugInfoPackage.debugClusterInfoPackage);
    }

    private startDownload(url: string) {
        //TODO: include auth token? 
        const $form = $("#downloadInfoPackageForm");
        $form.attr("action", appUrl.baseUrl + url);
        $form.submit();
    }
}

export = infoPackage;