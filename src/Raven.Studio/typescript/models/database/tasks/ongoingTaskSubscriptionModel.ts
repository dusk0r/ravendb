﻿/// <reference path="../../../../typings/tsd.d.ts"/>
import appUrl = require("common/appUrl");
import router = require("plugins/router");
import ongoingTask = require("models/database/tasks/ongoingTaskModel");

// This model is used by the 'Ongoing Tasks List View'
class ongoingTaskSubscriptionModel extends ongoingTask {

    editUrl: KnockoutComputed<string>;
    collection = ko.observable<string>();
    timeOfLastClientActivity = ko.observable<string>(); 

    validationGroup: KnockoutValidationGroup; 
    showSubscriptionDetails = ko.observable(false);
    
    constructor(dto: Raven.Server.Web.System.OngoingTaskSubscription | Raven.Client.Documents.Subscriptions.SubscriptionState ) {
        super();

        this.listViewUpdate(dto);
        this.listViewInitializeObservables(); 
    }

    listViewInitializeObservables() {
        super.initializeObservables();

        const urls = appUrl.forCurrentDatabase();
        this.editUrl = urls.editSubscription(this.taskId, this.taskName());
    }

    listViewUpdate(dto: Raven.Server.Web.System.OngoingTaskSubscription | Raven.Client.Documents.Subscriptions.SubscriptionState) {

        // 1. Must pass the right data in case we are in Edit View flow
        if ('Criteria' in dto) {
            const dtoEditModel = dto as Raven.Client.Documents.Subscriptions.SubscriptionState;

            const state: Raven.Client.Server.Operations.OngoingTaskState = dtoEditModel.Disabled ? 'Disabled' : 'Enabled';
            const emptyNodeId: Raven.Client.Server.Operations.NodeId = { NodeTag: "", NodeUrl: "", ResponsibleNode: "" };

            const dtoListModel: Raven.Server.Web.System.OngoingTaskSubscription = {
                Collection: dtoEditModel.Criteria.Collection,
                TimeOfLastClientActivity: dto.TimeOfLastClientActivity,
                ResponsibleNode: emptyNodeId,
                TaskConnectionStatus: 'Active', // todo: this has to be reviewed...
                TaskId: dtoEditModel.SubscriptionId,
                TaskName: dtoEditModel.SubscriptionName,
                TaskState: state,
                TaskType: 'Subscription'
            };

            super.update(dtoListModel);
            this.collection(dtoListModel.Collection);
        }
        // 2. List View flow
        else {
            super.update(dto as Raven.Server.Web.System.OngoingTaskSubscription);
            this.timeOfLastClientActivity(dto.TimeOfLastClientActivity);
            this.collection((dto as Raven.Server.Web.System.OngoingTaskSubscription).Collection);
        }
    }

    editTask() {
        router.navigate(this.editUrl());
    }
}

export = ongoingTaskSubscriptionModel;